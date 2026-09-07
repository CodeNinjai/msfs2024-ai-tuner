using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using AiTuner.Core;
using AiTuner.Core.Msfs;
using AiTuner.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiTuner.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    public static MainViewModel Instance { get; } = new();

    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private string _statusText = "System wird analysiert …";
    [ObservableProperty] private string _headerSubtitle = "";
    [ObservableProperty] private bool _hasWarnings;
    [ObservableProperty] private bool _hasAddonIssues;
    [ObservableProperty] private MsfsInstallation? _selectedInstallation;
    [ObservableProperty] private string _msfsFilter = "";
    [ObservableProperty] private string _msfsSummary = "";
    [ObservableProperty] private bool _hideVrSettings = true;
    [ObservableProperty] private bool _showOnlyEditable;
    [ObservableProperty] private string _addonFilter = "";
    [ObservableProperty] private string _addonSummary = "";
    [ObservableProperty] private string _nvidiaSummary = "";
    [ObservableProperty] private string _recommendationSummary = "";

    public ObservableCollection<CardItem> HardwareCards { get; } = new();
    public ObservableCollection<StatusItem> InstallationStatus { get; } = new();
    public ObservableCollection<CacheRow> CacheRows { get; } = new();
    public ObservableCollection<string> Warnings { get; } = new();
    public ObservableCollection<MsfsInstallation> Installations { get; } = new();
    public ObservableCollection<object> MsfsRows { get; } = new();
    public ObservableCollection<object> AddonRows { get; } = new();
    public ObservableCollection<object> WindowsRows { get; } = new();
    public ObservableCollection<object> NvidiaRows { get; } = new();
    public ObservableCollection<object> RecommendationRows { get; } = new();

    public AnalysisResult? Analysis { get; private set; }

    private readonly Dictionary<string, UserCfgDocument?> _cfgCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, SettingDefinition> MsfsCatalog = SettingsCatalog.ForArea("MSFS");
    private static readonly IReadOnlyDictionary<string, SettingDefinition> WindowsCatalog = SettingsCatalog.ForArea("Windows");
    private static readonly IReadOnlyDictionary<string, SettingDefinition> NvidiaCatalog = SettingsCatalog.ForArea("NVIDIA");

    public async Task LoadAsync()
    {
        IsLoading = true;
        StatusText = "System wird analysiert …";
        var result = await Task.Run(SystemAnalyzer.Run);
        Analysis = result;
        _cfgCache.Clear();
        Build(result);
        RefreshProfiles();
        RefreshBenchmarkState();
        RefreshLibraries();
        RefreshTuningChecklist();
        RefreshFlightMode();
        RefreshAutostart();
        RefreshContent();
        IsLoading = false;
    }

    private void Build(AnalysisResult analysis)
    {
        BuildHardwareCards(analysis);
        BuildInstallations(analysis);
        BuildCaches(analysis);
        BuildWarnings(analysis);
        BuildWindowsRows(analysis);
        BuildNvidiaRows(analysis);
        BuildRecommendations(analysis);
        RebuildAddonRows();

        HeaderSubtitle = $"Analyse vom {analysis.Timestamp:dd.MM.yyyy HH:mm} Uhr · {analysis.Hardware.OsDescription}";
    }

    private void BuildHardwareCards(AnalysisResult analysis)
    {
        HardwareCards.Clear();
        var hw = analysis.Hardware;

        if (hw.Cpu is { } cpu)
            HardwareCards.Add(new CardItem("Prozessor", cpu.Name,
                $"{cpu.Manufacturer} · {cpu.Cores} Kerne / {cpu.Threads} Threads · Basistakt {cpu.MaxClockMhz / 1000.0:0.0} GHz · L3-Cache {cpu.L3CacheKb / 1024} MB",
                "IconCpu", "#33FF8C42", "#FFFF8C42"));

        if (hw.Memory is { } memory)
            HardwareCards.Add(new CardItem("Arbeitsspeicher", $"{memory.TotalGb:0.#} GB",
                $"{memory.ModuleCount} Module · {memory.SpeedMtps} MT/s",
                "IconMemory", "#336CCB5F", "#FF6CCB5F"));

        foreach (var gpu in hw.Gpus)
            HardwareCards.Add(new CardItem("Grafikkarte", gpu.Name,
                $"{gpu.VramMb / 1024.0:0.#} GB VRAM · Treiber {gpu.DriverVersion} · Resizable BAR: {gpu.ResizableBarState}",
                "IconGpu", "#3376B900", "#FF9BE32E"));

        var primary = hw.Displays.FirstOrDefault(d => d.IsPrimary) ?? hw.Displays.FirstOrDefault();
        if (primary is not null)
            HardwareCards.Add(new CardItem("Anzeige", $"{primary.Width} × {primary.Height}",
                $"{primary.RefreshHz} Hz · {hw.Displays.Count} Monitor(e) · {primary.DeviceName}",
                "IconMonitor", "#33B39DFF", "#FFB39DFF"));

        if (hw.Volumes.Count > 0)
            HardwareCards.Add(new CardItem("Laufwerke", $"{hw.Volumes.Count} Volume(s)",
                string.Join("   ", hw.Volumes.Select(v => $"{v.DriveLetter} {v.BusType}/{v.MediaType} · {v.FreeGb:0}/{v.TotalGb:0} GB frei")),
                "IconHardDrive", "#33FFB900", "#FFFFB900"));

        // Sechste Kachel: Windows mit den drei tuning-relevanten Schaltern auf einen Blick.
        string WinValue(string key) => analysis.WindowsSettings.Items
            .FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase))?.Value ?? "?";
        HardwareCards.Add(new CardItem("Windows",
            hw.OsDescription.Replace("Microsoft ", ""),
            $"Energieplan: {WinValue("powerplan")} · Spielemodus: {WinValue("gamemode")} · HAGS: {WinValue("hags")}",
            "IconSettings", "#3300A4EF", "#FF41C0FF"));
    }

    private void BuildInstallations(AnalysisResult analysis)
    {
        InstallationStatus.Clear();
        Installations.Clear();
        foreach (var installation in analysis.MsfsInstallations)
        {
            InstallationStatus.Add(new StatusItem(installation.EditionName, installation.UserCfgExists,
                installation.UserCfgExists ? installation.UserCfgPath : "UserCfg.opt nicht gefunden"));
            if (installation.UserCfgExists)
                Installations.Add(installation);
        }
        SelectedInstallation = Installations.FirstOrDefault();
    }

    private void BuildCaches(AnalysisResult analysis)
    {
        CacheRows.Clear();
        foreach (var cache in analysis.Caches)
        {
            var isRollingCache = cache.Name.StartsWith("MSFS Rolling Cache", StringComparison.OrdinalIgnoreCase);
            CacheRows.Add(new CacheRow(
                cache.Name,
                cache.Exists ? $"{cache.SizeMb:#,0} MB" : "nicht vorhanden",
                cache.Path,
                cache.Exists && cache.SizeMb > 0,
                isRollingCache));
        }
    }

    private void BuildWarnings(AnalysisResult analysis)
    {
        Warnings.Clear();
        foreach (var warning in analysis.Hardware.Warnings
                     .Concat(analysis.WindowsSettings.Warnings)
                     .Concat(analysis.Nvidia.Warnings))
            Warnings.Add(warning);
        HasWarnings = Warnings.Count > 0;
    }

    private void BuildWindowsRows(AnalysisResult analysis)
    {
        WindowsRows.Clear();
        string? category = null;
        foreach (var item in analysis.WindowsSettings.Items)
        {
            if (item.Category != category)
            {
                WindowsRows.Add(new GroupHeader(item.Category));
                category = item.Category;
            }

            if (item.Key is not null && WindowsCatalog.TryGetValue(item.Key, out var definition))
                WindowsRows.Add(new EditableSettingRow(definition, RawWindowsValue(item), ApplySettingAsync));
            else
                WindowsRows.Add(new SettingRow(item.Name, item.Value, item.Detail));
        }
    }

    /// <summary>Extracts the raw registry value ("= 1" from the detail) resp. the power-plan GUID for the editor.</summary>
    private static string RawWindowsValue(Core.Windows.WindowsSettingItem item)
    {
        if (item.Key == "powerplan")
            return item.Detail ?? "";
        var match = Regex.Match(item.Detail ?? "", @"=\s*(-?\d+)$");
        return match.Success ? match.Groups[1].Value : item.Value;
    }

    private void BuildNvidiaRows(AnalysisResult analysis)
    {
        NvidiaRows.Clear();
        var nvidia = analysis.Nvidia;
        var gpu = analysis.Hardware.Gpus.FirstOrDefault();
        NvidiaSummary = gpu is null
            ? "Keine NVIDIA-GPU erkannt."
            : $"{gpu.Name} · Treiber {gpu.DriverVersion} · Resizable BAR: {gpu.ResizableBarState}";

        if (!nvidia.Available)
        {
            NvidiaRows.Add(new GroupHeader("Treiberprofile"));
            NvidiaRows.Add(new SettingRow("Status", "NVAPI nicht verfügbar", nvidia.Warnings.FirstOrDefault()));
        }
        else
        {
            NvidiaRows.Add(new GroupHeader("Basisprofil (alle 3D-Anwendungen)"));
            if (nvidia.GlobalSettings.Count == 0)
                NvidiaRows.Add(new SettingRow("Keine explizit gesetzten Einstellungen", "Treiber-Standardwerte aktiv"));
            foreach (var setting in nvidia.GlobalSettings)
                NvidiaRows.Add(new SettingRow(setting.Name, setting.Value, setting.Id));

            NvidiaRows.Add(new GroupHeader(nvidia.MsfsProfileName is null
                ? "MSFS-Spielprofil (nicht gefunden)"
                : $"Spielprofil: {nvidia.MsfsProfileName}"));
            foreach (var setting in nvidia.MsfsSettings)
            {
                var slug = Regex.Replace(setting.Name, "[^A-Za-z0-9]", "").ToLowerInvariant();
                if (NvidiaCatalog.TryGetValue(slug, out var definition))
                    NvidiaRows.Add(new EditableSettingRow(definition, setting.Numeric?.ToString() ?? setting.Value, ApplySettingAsync));
                else
                    NvidiaRows.Add(new SettingRow(setting.Name, setting.Value, setting.Id));
            }
        }

        NvidiaRows.Add(new GroupHeader("DLSS-Laufzeiten"));
        if (analysis.DlssRuntimes.Count == 0)
            NvidiaRows.Add(new SettingRow("Keine DLSS-DLLs gefunden", "Store-Pakete sind ggf. zugriffsgeschützt"));
        foreach (var dlss in analysis.DlssRuntimes)
            NvidiaRows.Add(new SettingRow($"{dlss.FileName} · {dlss.Source}", dlss.Version, dlss.Path));
    }

    private void BuildRecommendations(AnalysisResult analysis)
    {
        RecommendationRows.Clear();
        TuningCandidates.Clear();
        var recommendations = analysis.Recommendations;

        var high = recommendations.Count(r => r.Severity == Core.Rules.RecommendationSeverity.High);
        var medium = recommendations.Count(r => r.Severity == Core.Rules.RecommendationSeverity.Medium);
        var info = recommendations.Count - high - medium;
        RecommendationSummary = recommendations.Count == 0
            ? "Keine Auffälligkeiten — das System ist bereits gut eingestellt."
            : $"{recommendations.Count} Vorschläge · {high} hoch · {medium} mittel · {info} Hinweise";

        foreach (var group in recommendations.GroupBy(r => r.Severity).OrderByDescending(g => g.Key))
        {
            var title = group.Key switch
            {
                Core.Rules.RecommendationSeverity.High => "Priorität: Hoch",
                Core.Rules.RecommendationSeverity.Medium => "Priorität: Mittel",
                _ => "Hinweise",
            };
            RecommendationRows.Add(new GroupHeader($"{title} ({group.Count()})"));
            foreach (var recommendation in group)
            {
                if (recommendation.ActionJson is not null)
                    TuningCandidates.Add(new RecommendationRow(
                        recommendation.SeverityLabel, recommendation.Category,
                        $"[{recommendation.Category}] {recommendation.Title}",
                        recommendation.Current, recommendation.Recommended, recommendation.Reason,
                        recommendation.ApplyHint, recommendation.ActionJson,
                        recommendation.Details, recommendation.Evidence, recommendation.Id));
                RecommendationRows.Add(new RecommendationRow(
                    recommendation.SeverityLabel,
                    recommendation.Category,
                    recommendation.Title,
                    recommendation.Current,
                    recommendation.Recommended,
                    recommendation.Reason,
                    recommendation.ApplyHint,
                    recommendation.ActionJson,
                    recommendation.Details,
                    recommendation.Evidence,
                    recommendation.Id));
            }
        }

        if (SelectedCandidate is null || !TuningCandidates.Contains(SelectedCandidate))
            SelectedCandidate = TuningCandidates.FirstOrDefault();

        RecCount = recommendations.Count.ToString();
        RecHighText = $"{high} hoch";
        RecMediumText = $"{medium} mittel";
        RecInfoText = $"{info} Hinweise";
    }

    private UserCfgDocument? GetUserCfg(MsfsInstallation installation)
    {
        if (!_cfgCache.TryGetValue(installation.UserCfgPath, out var document))
        {
            document = installation.LoadUserCfg();
            _cfgCache[installation.UserCfgPath] = document;
        }
        return document;
    }

    private void RebuildMsfsRows()
    {
        MsfsRows.Clear();
        if (SelectedInstallation is null)
        {
            MsfsSummary = "Keine MSFS-2024-Installation mit UserCfg.opt gefunden.";
            return;
        }

        var document = GetUserCfg(SelectedInstallation);
        if (document is null)
        {
            MsfsSummary = "UserCfg.opt konnte nicht gelesen werden.";
            return;
        }

        var filter = MsfsFilter.Trim();
        string? currentGroup = null;
        var count = 0;

        // Erste Passe: aktuelle Werte je Match-Key für die Abhängigkeits-Sperren (dependsOn).
        var entries = document.Flatten().ToList();
        var currentValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var key = entry.Section.Length == 0
                ? entry.Key.ToLowerInvariant()
                : $"{entry.Section.Replace(" › ", "/").Replace(" ", "")}/{entry.Key}".ToLowerInvariant();
            currentValues[key] = entry.Value.Trim().Trim('"');
        }

        foreach (var entry in entries)
        {
            // VR-Zweig ausblenden (GraphicsVR-Sektion + "…VR"-Video-Keys), solange kein Headset genutzt wird.
            if (HideVrSettings
                && (entry.Section.StartsWith("GraphicsVR", StringComparison.OrdinalIgnoreCase)
                    || entry.Key.EndsWith("VR", StringComparison.Ordinal)))
                continue;

            var matchKey = entry.Section.Length == 0
                ? entry.Key.ToLowerInvariant()
                : $"{entry.Section.Replace(" › ", "/").Replace(" ", "")}/{entry.Key}".ToLowerInvariant();
            var isEditable = MsfsCatalog.TryGetValue(matchKey, out var definition);

            if (ShowOnlyEditable && !isEditable)
                continue;

            if (filter.Length > 0
                && !entry.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !entry.Value.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !entry.Section.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !(isEditable && definition!.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                continue;

            var group = entry.Section.Length == 0 ? "Allgemein" : entry.Section;
            if (group != currentGroup)
            {
                MsfsRows.Add(new GroupHeader(group));
                currentGroup = group;
            }

            if (isEditable)
            {
                // dependsOnValue: aktiv NUR wenn Abhängigkeit == Wert; dependsOnNotValue: gesperrt WENN == Wert.
                var rowEnabled = definition!.DependsOnMatch is null
                    || (definition.DependsOnNotValue is not null
                        ? !(currentValues.TryGetValue(definition.DependsOnMatch, out var notValue)
                            && string.Equals(notValue, definition.DependsOnNotValue, StringComparison.OrdinalIgnoreCase))
                        : currentValues.TryGetValue(definition.DependsOnMatch, out var depValue)
                          && string.Equals(depValue, definition.DependsOnValue, StringComparison.OrdinalIgnoreCase));
                MsfsRows.Add(new EditableSettingRow(definition, entry.Value, ApplySettingAsync, rowEnabled));
            }
            else
            {
                MsfsRows.Add(new SettingRow(entry.Key, entry.Value.Trim('"')));
            }
            count++;
        }

        MsfsSummary = $"{count} Einstellungen · {SelectedInstallation.UserCfgPath}";
    }

    private void RebuildAddonRows()
    {
        AddonRows.Clear();
        if (Analysis is null)
            return;

        var scan = Analysis.Addons;
        HasAddonIssues = scan.Issues.Count > 0;

        var multiConflicts = scan.IcaoConflicts.Where(c => c.Packages.Count >= 2).ToList();
        if (multiConflicts.Count > 0)
        {
            AddonRows.Add(new GroupHeader($"Airport-Konflikte — heuristische ICAO-Analyse ({multiConflicts.Count})"));
            foreach (var conflict in multiConflicts)
                AddonRows.Add(new SettingRow(
                    conflict.InvolvesOfficial ? $"{conflict.Icao} (auch als Official-Airport vorhanden)" : conflict.Icao,
                    string.Join("  +  ", conflict.Packages),
                    "Pro Airport sollte nur eine Scenery aktiv sein — Doppel-Gebäude/Z-Fighting/Performance."));
        }

        if (scan.Issues.Count > 0)
        {
            AddonRows.Add(new GroupHeader($"Mögliche Konflikte ({scan.Issues.Count})"));
            foreach (var issue in scan.Issues)
                AddonRows.Add(new SettingRow(issue, ""));
        }

        // Reine Info, kein Problem: Community hat Vorrang, der Sim blendet den Default-Airport aus.
        var overrides = scan.IcaoConflicts.Where(c => c.Packages.Count == 1 && c.InvolvesOfficial).ToList();
        if (overrides.Count > 0)
        {
            AddonRows.Add(new GroupHeader($"Ersetzte Official-Airports ({overrides.Count}) — normal, deine Scenery hat Vorrang"));
            foreach (var conflict in overrides)
                AddonRows.Add(new SettingRow($"{conflict.Icao} — {conflict.Packages[0]}",
                    "ersetzt den Standard-Airport des Sims"));
        }

        var filter = AddonFilter.Trim();
        var shown = 0;
        foreach (var group in scan.Addons
                     .GroupBy(a => a.ContentType, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var matches = group
                .Where(a => filter.Length == 0
                    || a.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || a.PackageName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || a.Creator.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(a => a.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (matches.Count == 0)
                continue;

            AddonRows.Add(new GroupHeader($"{group.Key} ({matches.Count})"));
            foreach (var addon in matches)
            {
                var value = string.Join(" · ", new[]
                {
                    addon.Version,
                    addon.Creator,
                    addon.HasWasm ? "WASM" : null,
                    addon.Edition,
                }.Where(s => !string.IsNullOrWhiteSpace(s)));
                AddonRows.Add(new SettingRow(addon.Title, value, addon.Path));
            }
            shown += matches.Count;
        }

        var officialOverrides = scan.IcaoConflicts.Count(c => c.Packages.Count == 1 && c.InvolvesOfficial);
        AddonSummary = scan.Addons.Count == 0
            ? "Keine Community-Add-ons gefunden."
            : $"{shown} von {scan.Addons.Count} Add-ons · {scan.AirportCount} Airports erkannt (heuristisch)"
              + (officialOverrides > 0 ? $" · {officialOverrides} ersetzen Official-Airports (normal)" : "");
    }

    partial void OnSelectedInstallationChanged(MsfsInstallation? value) => RebuildMsfsRows();

    partial void OnMsfsFilterChanged(string value) => RebuildMsfsRows();

    partial void OnHideVrSettingsChanged(bool value) => RebuildMsfsRows();

    partial void OnShowOnlyEditableChanged(bool value) => RebuildMsfsRows();

    partial void OnAddonFilterChanged(string value) => RebuildAddonRows();
}
