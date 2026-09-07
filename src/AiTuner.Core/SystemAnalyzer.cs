using AiTuner.Core.Caches;
using AiTuner.Core.Hardware;
using AiTuner.Core.Msfs;
using AiTuner.Core.Nvidia;
using AiTuner.Core.Rules;
using AiTuner.Core.Windows;

namespace AiTuner.Core;

public sealed class AnalysisResult
{
    public required DateTimeOffset Timestamp { get; init; }
    public required HardwareSnapshot Hardware { get; init; }
    public required IReadOnlyList<MsfsInstallation> MsfsInstallations { get; init; }
    public required WindowsSettingsSnapshot WindowsSettings { get; init; }
    public required NvidiaSnapshot Nvidia { get; init; }
    public required IReadOnlyList<DlssRuntimeInfo> DlssRuntimes { get; init; }
    // set statt init: Bibliotheks-Aktionen (Deaktivieren/Verschieben/Installieren) tauschen den
    // Add-on-Scan aus, damit Empfehlungen/Übersicht ohne Voll-Analyse aktuell bleiben.
    public required AddonScanResult Addons { get; set; }
    public required IReadOnlyList<CacheEntry> Caches { get; init; }

    /// <summary>Filled by SystemAnalyzer after all snapshots are gathered (rules need the complete result).</summary>
    public IReadOnlyList<Recommendation> Recommendations { get; set; } = [];
}

public static class SystemAnalyzer
{
    /// <summary>Full read-only analysis. Blocking (WMI + NVAPI + disk); call from a background thread in UI apps.</summary>
    public static AnalysisResult Run()
    {
        var installations = MsfsLocator.DetectInstallations();

        var result = new AnalysisResult
        {
            Timestamp = DateTimeOffset.Now,
            Hardware = HardwareScanner.Scan(),
            MsfsInstallations = installations,
            WindowsSettings = WindowsSettingsReader.Read(),
            Nvidia = NvidiaReader.Read(),
            DlssRuntimes = DlssScanner.Scan(installations),
            Addons = AddonScanner.Scan(installations),
            Caches = CacheScanner.Scan(installations),
        };

        result.Recommendations = RuleEngine.Evaluate(result);
        return result;
    }
}
