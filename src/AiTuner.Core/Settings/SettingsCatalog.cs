using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiTuner.Core.Settings;

public sealed record SettingOption(string Value, string Label);

/// <summary>One editable setting: display metadata for the UI plus the write target (action template).</summary>
public sealed class SettingDefinition
{
    public string Id { get; set; } = "";
    /// <summary>MSFS | Windows | NVIDIA — decides which page picks the entry up.</summary>
    public string Area { get; set; } = "";
    /// <summary>Lookup key: MSFS = lowercased "section/key" path, Windows = item key, NVIDIA = name slug.</summary>
    public string Match { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>"enum" (options list) or "number" (free numeric with min/max).</summary>
    public string Kind { get; set; } = "enum";
    public List<SettingOption> Options { get; set; } = new();
    public double? Min { get; set; }
    public double? Max { get; set; }
    public string? RangeNote { get; set; }

    /// <summary>FPS-Kosten: none | low | medium | high | veryhigh.</summary>
    public string? Impact { get; set; }

    /// <summary>Hauptlast: cpu | gpu | both.</summary>
    public string? Load { get; set; }

    /// <summary>Abhängigkeit: dieses Setting ist nur aktiv, wenn das Setting mit DependsOnMatch den Wert DependsOnValue hat.</summary>
    public string? DependsOnMatch { get; set; }
    public string? DependsOnValue { get; set; }
    /// <summary>Umkehrung: gesperrt, WENN das Setting mit DependsOnMatch diesen Wert hat (z.B. Sim-VSync bei DLSS).</summary>
    public string? DependsOnNotValue { get; set; }

    public string ImpactLabel => Impact?.ToLowerInvariant() switch
    {
        "low" => "Niedrig",
        "medium" => "Mittel",
        "high" => "Hoch",
        "veryhigh" => "Sehr hoch",
        _ => "",
    };

    public string LoadLabel => Load?.ToLowerInvariant() switch
    {
        "cpu" => "CPU",
        "gpu" => "GPU",
        "both" => "CPU+GPU",
        _ => "",
    };
    /// <summary>Action template (like rule actions) without the value; SettingWriter injects it.</summary>
    public JsonElement Target { get; set; }

    public string RangeText
    {
        get
        {
            if (Kind == "number")
            {
                var range = Min is not null && Max is not null ? $"Wertebereich: {Min} – {Max}" : "";
                return string.Join(" · ", new[] { range, RangeNote }.Where(s => !string.IsNullOrWhiteSpace(s)));
            }
            var options = string.Join("  ·  ", Options.Select(o => $"{o.Value} = {o.Label}"));
            return string.Join(" · ", new[] { options, RangeNote }.Where(s => !string.IsNullOrWhiteSpace(s)));
        }
    }
}

public static class SettingsCatalog
{
    private static IReadOnlyList<SettingDefinition>? _cache;

    public static IReadOnlyList<SettingDefinition> All => _cache ??= Load();

    public static IReadOnlyDictionary<string, SettingDefinition> ForArea(string area)
        => All.Where(d => d.Area.Equals(area, StringComparison.OrdinalIgnoreCase))
              .ToDictionary(d => d.Match, d => d, StringComparer.OrdinalIgnoreCase);

    /// <summary>Video-Settings, die in der UserCfg einen "…VR"-Zwilling haben.</summary>
    private static readonly string[] VideoVrTwinKeys =
    [
        "antialiasing", "dlssmode", "fsrmode", "framegeneration", "nbframestogenerate",
        "reflex", "dynamicsettings", "targetframerate", "sharpenamount",
        "primaryscaling", "secondaryscaling",
    ];

    private static List<SettingDefinition> Load()
    {
        try
        {
            var json = LoadJson();
            if (json is null)
                return new List<SettingDefinition>();
            var definitions = JsonSerializer.Deserialize<List<SettingDefinition>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                ?? new List<SettingDefinition>();
            return ExpandVrVariants(definitions);
        }
        catch
        {
            return new List<SettingDefinition>();
        }
    }

    /// <summary>
    /// Klont alle MSFS-Graphics- und passenden Video-Einträge automatisch für den VR-Zweig
    /// (Sektion GraphicsVR bzw. Key-Suffix "VR"), damit der Katalog nicht doppelt gepflegt wird.
    /// </summary>
    private static List<SettingDefinition> ExpandVrVariants(List<SettingDefinition> definitions)
    {
        var expanded = new List<SettingDefinition>(definitions);
        foreach (var definition in definitions)
        {
            if (!definition.Area.Equals("MSFS", StringComparison.OrdinalIgnoreCase))
                continue;

            if (definition.Match.StartsWith("graphics/", StringComparison.OrdinalIgnoreCase))
            {
                expanded.Add(CloneForVr(definition,
                    "graphicsvr/" + definition.Match["graphics/".Length..],
                    sectionToVr: true, keySuffixVr: false));
            }
            else if (definition.Match.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                     && VideoVrTwinKeys.Contains(definition.Match["video/".Length..], StringComparer.OrdinalIgnoreCase))
            {
                expanded.Add(CloneForVr(definition, definition.Match + "vr",
                    sectionToVr: false, keySuffixVr: true));
            }
        }
        return expanded;
    }

    private static string? TransformMatchToVr(string? match)
    {
        if (match is null)
            return null;
        if (match.StartsWith("graphics/", StringComparison.OrdinalIgnoreCase))
            return "graphicsvr/" + match["graphics/".Length..];
        if (match.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            return match + "vr";
        return match;
    }

    private static SettingDefinition CloneForVr(SettingDefinition source, string newMatch, bool sectionToVr, bool keySuffixVr)
    {
        var target = JsonNode.Parse(source.Target.GetRawText())!.AsObject();
        if (sectionToVr && target["section"]?.GetValue<string>() is { } section
            && section.StartsWith("Graphics", StringComparison.OrdinalIgnoreCase))
            target["section"] = "GraphicsVR" + section["Graphics".Length..];
        if (keySuffixVr && target["key"]?.GetValue<string>() is { } key)
            target["key"] = key + "VR";

        using var document = JsonDocument.Parse(target.ToJsonString());
        return new SettingDefinition
        {
            Id = source.Id + "-vr",
            Area = source.Area,
            Match = newMatch,
            Name = source.Name + " (VR)",
            Description = source.Description,
            Kind = source.Kind,
            Options = source.Options,
            Min = source.Min,
            Max = source.Max,
            RangeNote = source.RangeNote,
            Impact = source.Impact,
            Load = source.Load,
            DependsOnMatch = TransformMatchToVr(source.DependsOnMatch),
            DependsOnValue = source.DependsOnValue,
            DependsOnNotValue = source.DependsOnNotValue,
            Target = document.RootElement.Clone(),
        };
    }

    private static string? LoadJson()
    {
        var overridePaths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "settings-catalog.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Msfs2024AiTuner", "settings-catalog.json"),
        };
        foreach (var path in overridePaths)
        {
            try
            {
                if (File.Exists(path))
                    return File.ReadAllText(path);
            }
            catch { }
        }

        using var stream = typeof(SettingsCatalog).Assembly.GetManifestResourceStream("AiTuner.Core.Settings.settings-catalog.json");
        if (stream is null)
            return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
