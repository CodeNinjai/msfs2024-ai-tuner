using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiTuner.Core.Rules;

/// <summary>
/// Evaluates the declarative JSON rule set against the fact bag. Rules live in the embedded
/// Rules/rules.json; an external "rules.json" next to the executable or under
/// %APPDATA%\Msfs2024AiTuner overrides it (community-updatable without a new release).
/// </summary>
public static class RuleEngine
{
    private sealed class RuleCondition
    {
        public string Fact { get; set; } = "";
        public string Op { get; set; } = "eq";
        public JsonElement Value { get; set; }
    }

    private sealed class RuleDefinition
    {
        public string Id { get; set; } = "";
        public string Category { get; set; } = "Allgemein";
        public string Severity { get; set; } = "info";
        public string Title { get; set; } = "";
        public List<RuleCondition> Conditions { get; set; } = new();
        public string Current { get; set; } = "";
        public string Recommended { get; set; } = "";
        public string Reason { get; set; } = "";
        public string? ApplyHint { get; set; }
        public JsonElement? Action { get; set; }
        public string? Details { get; set; }
    }

    public static IReadOnlyList<Recommendation> Evaluate(AnalysisResult analysis)
    {
        var facts = FactBag.From(analysis);
        var recommendations = new List<Recommendation>();

        List<RuleDefinition> rules;
        try
        {
            rules = LoadRules();
        }
        catch (Exception ex)
        {
            return
            [
                new Recommendation("engine-load-error", "System", RecommendationSeverity.Info,
                    "Regelwerk konnte nicht geladen werden", "—", "—",
                    $"{ex.GetType().Name}: {ex.Message}", null),
            ];
        }

        foreach (var rule in rules)
        {
            try
            {
                if (rule.Conditions.Count == 0 || !rule.Conditions.All(c => EvaluateCondition(c, facts)))
                    continue;

                recommendations.Add(new Recommendation(
                    rule.Id,
                    rule.Category,
                    ParseSeverity(rule.Severity),
                    facts.Substitute(rule.Title),
                    facts.Substitute(rule.Current),
                    facts.Substitute(rule.Recommended),
                    facts.Substitute(rule.Reason),
                    rule.ApplyHint is null ? null : facts.Substitute(rule.ApplyHint),
                    rule.Action?.ValueKind == JsonValueKind.Object ? rule.Action.Value.GetRawText() : null,
                    rule.Details is null ? null : facts.Substitute(rule.Details),
                    BuildEvidence(rule.Conditions, facts)));
            }
            catch
            {
                // Fehlerhafte Einzelregel (z.B. aus externem Regelwerk) darf die Analyse nicht stoppen.
            }
        }

        return recommendations
            .OrderByDescending(r => r.Severity)
            .ThenBy(r => r.Category, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Live-Belege: welche Fakten mit welchen Ist-Werten die Regel ausgelöst haben.</summary>
    private static List<string> BuildEvidence(List<RuleCondition> conditions, FactBag facts)
    {
        var evidence = new List<string>();
        foreach (var condition in conditions)
        {
            var actual = facts.Get(condition.Fact) ?? "—";
            var op = condition.Op.ToLowerInvariant();
            var expectation = op switch
            {
                "exists" => "Bedingung: vorhanden",
                "nexists" => "Bedingung: nicht vorhanden",
                "eq" => $"Bedingung: = {ElementString(condition.Value)}",
                "ne" => $"Bedingung: ≠ {ElementString(condition.Value)}",
                "lt" => $"Bedingung: < {ElementString(condition.Value)}",
                "lte" => $"Bedingung: ≤ {ElementString(condition.Value)}",
                "gt" => $"Bedingung: > {ElementString(condition.Value)}",
                "gte" => $"Bedingung: ≥ {ElementString(condition.Value)}",
                "contains" => $"Bedingung: enthält {ElementString(condition.Value)}",
                "ncontains" => $"Bedingung: enthält nicht {ElementString(condition.Value)}",
                "in" => "Bedingung: in Liste",
                "nin" => "Bedingung: nicht in Liste",
                "matches" => $"Bedingung: Muster {ElementString(condition.Value)}",
                _ => $"Bedingung: {op}",
            };
            evidence.Add($"{condition.Fact} = {actual}    ({expectation})");
        }
        return evidence;
    }

    private static RecommendationSeverity ParseSeverity(string severity) => severity.ToLowerInvariant() switch
    {
        "high" => RecommendationSeverity.High,
        "medium" => RecommendationSeverity.Medium,
        _ => RecommendationSeverity.Info,
    };

    private static bool EvaluateCondition(RuleCondition condition, FactBag facts)
    {
        var value = facts.Get(condition.Fact);
        var op = condition.Op.ToLowerInvariant();

        if (op == "exists")
            return value is not null;
        if (op == "nexists")
            return value is null;
        if (value is null)
            return false;

        switch (op)
        {
            case "eq":
                return ValueEquals(value, condition.Value);
            case "ne":
                return !ValueEquals(value, condition.Value);
            case "lt":
            case "lte":
            case "gt":
            case "gte":
                if (!TryNumber(value, out var actual) || !TryElementNumber(condition.Value, out var expected))
                    return false;
                return op switch
                {
                    "lt" => actual < expected,
                    "lte" => actual <= expected,
                    "gt" => actual > expected,
                    _ => actual >= expected,
                };
            case "contains":
                return value.Contains(ElementString(condition.Value), StringComparison.OrdinalIgnoreCase);
            case "ncontains":
                return !value.Contains(ElementString(condition.Value), StringComparison.OrdinalIgnoreCase);
            case "in":
                return EnumerateArray(condition.Value).Any(e => ValueEquals(value, e));
            case "nin":
                return !EnumerateArray(condition.Value).Any(e => ValueEquals(value, e));
            case "matches":
                return Regex.IsMatch(value, ElementString(condition.Value), RegexOptions.IgnoreCase);
            default:
                return false;
        }
    }

    private static bool ValueEquals(string actual, JsonElement expected)
    {
        if (expected.ValueKind == JsonValueKind.Number && TryNumber(actual, out var actualNumber))
            return Math.Abs(actualNumber - expected.GetDouble()) < 0.0001;
        return string.Equals(actual, ElementString(expected), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryNumber(string value, out double number)
        => double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out number);

    private static bool TryElementNumber(JsonElement element, out double number)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            number = element.GetDouble();
            return true;
        }
        return TryNumber(ElementString(element), out number);
    }

    private static string ElementString(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? "",
        JsonValueKind.Number => element.GetDouble().ToString(CultureInfo.InvariantCulture),
        JsonValueKind.True => "1",
        JsonValueKind.False => "0",
        _ => element.GetRawText(),
    };

    private static IEnumerable<JsonElement> EnumerateArray(JsonElement element)
        => element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : [element];

    /// <summary>Pfad der Community-Override-Datei (gewinnt gegen das eingebettete Regelwerk).</summary>
    public static string OverridePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Msfs2024AiTuner", "rules.json");

    public static bool HasOverride => File.Exists(OverridePath);

    /// <summary>Das aktuell wirksame Regelwerk als JSON (Override oder eingebettet).</summary>
    public static string ActiveRulesJson() => LoadRulesJson() ?? "[]";

    /// <summary>Prüft, ob ein JSON als Regelwerk ladbar ist; liefert die Regelanzahl.</summary>
    public static (bool Ok, int Count, string Message) ValidateRulesJson(string json)
    {
        try
        {
            var rules = JsonSerializer.Deserialize<List<RuleDefinition>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (rules is null || rules.Count == 0)
                return (false, 0, "Datei enthält keine Regeln.");
            var invalid = rules.Count(r => string.IsNullOrWhiteSpace(r.Id) || string.IsNullOrWhiteSpace(r.Title));
            return invalid > 0
                ? (false, rules.Count, $"{invalid} Regel(n) ohne id/title.")
                : (true, rules.Count, $"{rules.Count} Regeln gültig.");
        }
        catch (Exception ex)
        {
            return (false, 0, $"Kein gültiges Regelwerk: {ex.Message}");
        }
    }

    private static List<RuleDefinition> LoadRules()
    {
        var json = LoadRulesJson()
            ?? throw new InvalidOperationException("Eingebettetes Regelwerk (rules.json) nicht gefunden.");

        return JsonSerializer.Deserialize<List<RuleDefinition>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
            ?? new List<RuleDefinition>();
    }

    private static string? LoadRulesJson()
    {
        var overridePaths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "rules.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Msfs2024AiTuner", "rules.json"),
        };
        foreach (var path in overridePaths)
        {
            if (File.Exists(path))
                return File.ReadAllText(path);
        }

        using var stream = typeof(RuleEngine).Assembly.GetManifestResourceStream("AiTuner.Core.Rules.rules.json");
        if (stream is null)
            return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
