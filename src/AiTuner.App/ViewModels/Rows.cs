namespace AiTuner.App.ViewModels;

public sealed record CardItem(string Title, string Value, string Detail,
    string IconKey = "", string TileBg = "#334CC2FF", string TileFg = "#FF4CC2FF");

public sealed record StatusItem(string Title, bool Ok, string Detail);

/// <summary>Section header row inside settings lists (rendered via implicit DataTemplate).</summary>
public sealed record GroupHeader(string Title);

/// <summary>Name/value row inside settings lists; Detail becomes the tooltip.</summary>
public sealed record SettingRow(string Name, string Value, string? Detail = null);

/// <summary>One recommendation card on the Empfehlungen page.</summary>
public sealed record RecommendationRow(
    string SeverityLabel,
    string Category,
    string Title,
    string Current,
    string Recommended,
    string Reason,
    string? ApplyHint,
    string? ActionJson,
    string? Details = null,
    IReadOnlyList<string>? Evidence = null,
    string RuleId = "")
{
    public bool HasAction => ActionJson is not null;
    public bool HasDetails => !string.IsNullOrWhiteSpace(Details);
    public string RuleIdText => $"Regel-ID: {RuleId} (rules.json — extern anpassbar)";
}

/// <summary>Cache entry on the dashboard, with optional clear button.</summary>
public sealed record CacheRow(string Name, string SizeText, string Path, bool CanClear, bool IsFile);

/// <summary>Saved profile/backup in the Profiles page list.</summary>
public sealed record ProfileItem(string Name, string CreatedText, string TypeLabel, bool IsBackup);

/// <summary>Benchmark result row with baseline deltas for the comparison table.</summary>
public sealed record BenchmarkRow(
    AiTuner.Core.Benchmark.BenchmarkResult Result,
    string Label,
    string ProfileText,
    string TimeText,
    string AvgFps,
    string LowFps,
    string DeltaFps,
    string DeltaLow,
    bool DeltaFpsGood,
    bool DeltaLowGood,
    bool IsBaseline,
    string DetailTooltip);
