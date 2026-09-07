namespace AiTuner.Core.Rules;

public enum RecommendationSeverity
{
    Info = 0,
    Medium = 1,
    High = 2,
}

public sealed record Recommendation(
    string Id,
    string Category,
    RecommendationSeverity Severity,
    string Title,
    string Current,
    string Recommended,
    string Reason,
    string? ApplyHint,
    string? ActionJson = null,
    string? Details = null,
    IReadOnlyList<string>? Evidence = null)
{
    public string SeverityLabel => Severity switch
    {
        RecommendationSeverity.High => "Hoch",
        RecommendationSeverity.Medium => "Mittel",
        _ => "Hinweis",
    };
}
