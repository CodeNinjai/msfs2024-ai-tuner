using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using AiTuner.Core.Libraries;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiTuner.App.ViewModels;

public sealed record LibraryRowItem(
    LibraryAddon Addon,
    string Display,
    string Meta,
    string StateLabel,
    bool StateActive,
    bool CanActivate,
    bool CanDeactivate,
    bool CanMove,
    string StateTooltip = "",
    bool GsxVisible = false,
    bool GsxHasProfile = false,
    string GsxTooltip = "",
    string GsxSearchIcao = "");

/// <summary>Warnhinweis-Zeile (z.B. Dublette) — mit Warnsymbol dargestellt.</summary>
public sealed record WarningRow(string Text);

public sealed record LibraryListItem(string Path, string Name, bool AutoDetected, bool CanRemove, int AddonCount, int ActiveCount,
    string ManagerKind = "")
{
    public bool Managed => ManagerKind.Length > 0;
    public bool CanRestructure => !Managed;
    public string SubText => $"{Path} · {AddonCount} Add-on(s) · {ActiveCount} aktiv"
        + (Managed ? $" · verwaltet durch {ManagerKind}" : "");
}

/// <summary>Szenerie-Bibliotheken: Auslagern, Aktivieren/Deaktivieren per Junction, Dubletten.</summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<object> LibraryRows { get; } = new();
    public ObservableCollection<LibraryListItem> LibraryList { get; } = new();
    public ObservableCollection<string> LibraryPathChoices { get; } = new();

    [ObservableProperty] private string? _selectedTargetLibrary;
    [ObservableProperty] private string _librarySummary = "";
    [ObservableProperty] private string _libraryFilter = "";

    partial void OnLibraryFilterChanged(string value) => RefreshLibraries();

    private string? _communityPath;
    private string? _community2024Path;
    private Dictionary<string, List<string>>? _gsxProfiles; // null = GSX nicht installiert
    private List<string> _allPackageNames = new(); // für VDGS-Add-on-Erkennung

    public bool HasCommunity2024 => _community2024Path is not null;

    /// <summary>
    /// Scannt nach Add-on-Aktionen nur den Add-on-Teil der Analyse neu und baut Empfehlungen
    /// und Add-on-Seite um — sonst zeigen die Empfehlungen den veralteten Stand vom App-Start
    /// (z.B. bereits aufgeräumte Dubletten).
    /// </summary>
    private async Task RefreshAddonAnalysisAsync()
    {
        if (Analysis is null)
            return;
        try
        {
            var addons = await Task.Run(() => Core.Msfs.AddonScanner.Scan(Analysis.MsfsInstallations));
            Analysis.Addons = addons;
            BuildRecommendations(Analysis);
            RebuildAddonRows();
        }
        catch { }
    }

    /// <summary>Reichert eine Zeile um den GSX-Profil-Status an (nur wenn GSX installiert und ICAO erkennbar).</summary>
    private LibraryRowItem WithGsx(LibraryRowItem row)
    {
        if (_gsxProfiles is null || row.Addon.IsBroken || row.Addon.IsAircraftish)
            return row; // Flugzeuge/Liveries bekommen keinen GSX-Airport-Chip

        var icaos = Core.Msfs.IcaoAnalyzer.ExtractIcaos(row.Addon.Title, row.Addon.FolderName, Array.Empty<string>());
        if (icaos.Count == 0)
            return row;

        var found = icaos
            .Where(i => _gsxProfiles.ContainsKey(i))
            .SelectMany(i => _gsxProfiles[i])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return row with
        {
            GsxVisible = true,
            GsxHasProfile = found.Count > 0,
            GsxSearchIcao = icaos[0],
            GsxTooltip = found.Count > 0
                ? "GSX-Profil vorhanden:\n" + string.Join("\n", found) + "\n\nKlick: GSX-Profilordner öffnen"
                : $"Kein GSX-Profil für {string.Join("/", icaos)} gefunden.\n\nKlick: auf flightsim.to nach einem Profil suchen (Installation dann einfach per Drag & Drop hierher).",
        };
    }

    [RelayCommand]
    private void OpenAddonFolder(LibraryRowItem? row)
    {
        if (row is null)
            return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", row.Addon.Path) { UseShellExecute = true });
        }
        catch { }
    }

    public void RefreshLibraries()
    {
        LibraryRows.Clear();
        LibraryList.Clear();
        LibraryPathChoices.Clear();

        var installation = Analysis?.MsfsInstallations.FirstOrDefault(i => i.UserCfgExists);
        if (installation is null)
        {
            LibrarySummary = "Keine MSFS-Installation gefunden.";
            return;
        }

        var scan = LibraryService.Scan(installation);
        _communityPath = scan.CommunityPath;
        _community2024Path = scan.Community2024Path;
        _gsxProfiles = GsxProfileService.IsGsxInstalled ? GsxProfileService.LoadProfilesByIcao() : null;
        _allPackageNames = scan.CommunityItems.Select(c => c.FolderName)
            .Concat(scan.LibraryItems.Select(l => l.FolderName)).ToList();
        if (_communityPath is null)
        {
            LibrarySummary = "Community-Ordner nicht gefunden.";
            return;
        }

        var libraryNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in scan.Libraries)
        {
            var items = scan.LibraryItems
                .Where(l => string.Equals(l.Location, library.Path, StringComparison.OrdinalIgnoreCase)).ToList();
            LibraryList.Add(new LibraryListItem(library.Path, library.Name, library.AutoDetected,
                CanRemove: !library.AutoDetected, items.Count, items.Count(i => i.IsActive),
                ManagerKind: library.ManagerKind));
            if (!library.Managed) // verwaltete Strukturen sind kein Verschiebe-Ziel
                LibraryPathChoices.Add(library.Path);
            libraryNames[library.Path] = library.Name;
        }
        if (SelectedTargetLibrary is null || !LibraryPathChoices.Contains(SelectedTargetLibrary))
            SelectedTargetLibrary = LibraryPathChoices.FirstOrDefault();

        var filter = LibraryFilter.Trim();
        var matches = (LibraryAddon a) => filter.Length == 0
            || a.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || a.FolderName.Contains(filter, StringComparison.OrdinalIgnoreCase);

        var duplicates = scan.Duplicates
            .Where(d => filter.Length == 0 || d.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (duplicates.Count > 0)
        {
            LibraryRows.Add(new GroupHeader($"⚠ Dubletten — dasselbe Paket an mehreren Orten ({duplicates.Count})"));
            foreach (var duplicate in duplicates)
                LibraryRows.Add(new WarningRow(duplicate));
        }

        var isC2024 = (LibraryAddon a) => a.Location.Equals("Community2024", StringComparison.OrdinalIgnoreCase);
        var aircraftItems = scan.CommunityItems.Where(c => !c.IsLink && c.IsAircraftish && matches(c)).OrderBy(c => c.Title, StringComparer.OrdinalIgnoreCase).ToList();
        var fixedItems = scan.CommunityItems.Where(c => !c.IsLink && !c.IsAircraftish && !isC2024(c) && matches(c)).OrderBy(c => c.Title, StringComparer.OrdinalIgnoreCase).ToList();
        var fixed2024Items = scan.CommunityItems.Where(c => !c.IsLink && !c.IsAircraftish && isC2024(c) && matches(c)).OrderBy(c => c.Title, StringComparer.OrdinalIgnoreCase).ToList();
        var brokenItems = scan.CommunityItems.Where(c => c.IsLink && c.IsBroken && matches(c)).OrderBy(c => c.FolderName, StringComparer.OrdinalIgnoreCase).ToList();
        var linkedItems = scan.CommunityItems.Where(c => c.IsLink && !c.IsBroken && matches(c)).OrderBy(c => c.Title, StringComparer.OrdinalIgnoreCase).ToList();

        if (brokenItems.Count > 0)
        {
            LibraryRows.Add(new GroupHeader($"⚠ Defekte Links — Ziel existiert nicht mehr ({brokenItems.Count})"));
            foreach (var item in brokenItems)
                LibraryRows.Add(new LibraryRowItem(item, item.FolderName,
                    $"Junction ins Leere ({item.Location}) — MSFS ignoriert den Eintrag; per „Deaktivieren“ gefahrlos entfernen (löscht nur den Link).",
                    "Link defekt", false,
                    CanActivate: false, CanDeactivate: true, CanMove: false));
        }

        // Verlinkte Add-ons erscheinen bei IHRER Bibliothek (Chip „Aktiv“) — hier nur noch das
        // Auffangnetz: Links, deren Ziel von keiner erkannten Bibliothek abgedeckt wird.
        var coveredNames = new HashSet<string>(
            scan.LibraryItems.Where(l => l.IsActive).Select(l => l.FolderName),
            StringComparer.OrdinalIgnoreCase);
        var leftoverLinks = linkedItems.Where(l => !coveredNames.Contains(l.FolderName)).ToList();
        if (leftoverLinks.Count > 0)
        {
            LibraryRows.Add(new GroupHeader($"Per Link aktiv — Ziel außerhalb erkannter Bibliotheken ({leftoverLinks.Count})"));
            foreach (var item in leftoverLinks)
                LibraryRows.Add(WithGsx(new LibraryRowItem(item, item.Title,
                    $"{item.FolderName}{VersionText(item)}", "Aktiv (Link)", true,
                    CanActivate: false, CanDeactivate: true, CanMove: false,
                    StateTooltip: $"Per Link im {item.Location}-Ordner aktiv")));
        }

        if (fixedItems.Count > 0 || filter.Length == 0)
            LibraryRows.Add(new GroupHeader($"Community-Ordner — fest installiert, kein Link ({fixedItems.Count})"));
        foreach (var item in fixedItems)
            LibraryRows.Add(WithGsx(new LibraryRowItem(item, item.Title,
                $"{item.FolderName}{VersionText(item)}", "Community", true,
                CanActivate: false, CanDeactivate: true, CanMove: true,
                StateTooltip: "Fest installiert im Community-Ordner (MSFS 2020 + 2024) — aktiv")));

        if (aircraftItems.Count > 0)
        {
            LibraryRows.Add(new GroupHeader($"Flugzeuge, Liveries & Co. — fest im Community-Ordner ({aircraftItems.Count})"));
            foreach (var item in aircraftItems)
                LibraryRows.Add(new LibraryRowItem(item, item.Title,
                    $"{item.FolderName}{VersionText(item)} · {item.ContentType}", item.Location, true,
                    CanActivate: false, CanDeactivate: true, CanMove: true,
                    StateTooltip: $"Fest installiert ({item.Location}) — aktiv · kein Szenerie-Paket"));
        }

        if (_community2024Path is not null && fixed2024Items.Count > 0)
        {
            LibraryRows.Add(new GroupHeader($"Community2024-Ordner — nur MSFS 2024, fest installiert ({fixed2024Items.Count})"));
            foreach (var item in fixed2024Items)
                LibraryRows.Add(WithGsx(new LibraryRowItem(item, item.Title,
                    $"{item.FolderName}{VersionText(item)}", "Community2024", true,
                    CanActivate: false, CanDeactivate: true, CanMove: true,
                    StateTooltip: "Fest installiert im Community2024-Ordner (wird nur von MSFS 2024 geladen) — aktiv")));
        }

        foreach (var library in scan.Libraries)
        {
            var items = scan.LibraryItems.Where(l => string.Equals(l.Location, library.Path, StringComparison.OrdinalIgnoreCase) && matches(l))
                .OrderBy(l => l.Title, StringComparer.OrdinalIgnoreCase).ToList();
            if (items.Count == 0 && filter.Length > 0)
                continue;
            LibraryRows.Add(new GroupHeader(library.Managed
                ? $"Bibliothek „{library.Name}“ ({items.Count}) — {library.Path} · verwaltet durch {library.ManagerKind}, nicht umbauen"
                : $"Bibliothek „{library.Name}“ ({items.Count}) — {library.Path}"));
            if (items.Count == 0)
                LibraryRows.Add(new SettingRow("Keine Add-ons (Unterordner mit manifest.json) gefunden", ""));
            foreach (var item in items)
                LibraryRows.Add(WithGsx(new LibraryRowItem(item, item.Title,
                    $"{item.FolderName}{VersionText(item)}{(item.IsNested ? " · verschachtelt (liegt eine Ebene tiefer)" : "")}",
                    library.Name, item.IsActive,
                    CanActivate: !item.IsActive, CanDeactivate: item.IsActive, CanMove: !library.Managed,
                    StateTooltip: item.IsActive
                        ? $"Aktiv — per Link im {item.ActiveIn}-Ordner · Bibliothek „{library.Name}“"
                        : $"Inaktiv — kein Link in Community/Community2024 · Bibliothek „{library.Name}“")));
        }

        if (filter.Length > 0 && LibraryRows.Count == 0)
            LibraryRows.Add(new SettingRow($"Keine Treffer für „{filter}“", ""));

        var activeLinks = linkedItems.Count;
        var inactive = scan.LibraryItems.Count(l => !l.IsActive);
        var fixed2024Text = _community2024Path is null ? "" : $" · {fixed2024Items.Count} fest in Community2024";
        LibrarySummary = filter.Length > 0
            ? $"Filter „{filter}“ aktiv — {LibraryRows.Count(r => r is LibraryRowItem)} Treffer"
            : $"{scan.Libraries.Count} Bibliothek(en) · {activeLinks} per Link aktiv · {inactive} inaktiv in Bibliotheken · {fixedItems.Count} fest im Community-Ordner{fixed2024Text}";
    }

    private static string VersionText(LibraryAddon addon)
        => string.IsNullOrEmpty(addon.Version) ? "" : $" · v{addon.Version}";

    [RelayCommand]
    private void AddLibrary()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Bibliotheks-Ordner wählen (enthält Add-on-Unterordner)",
        };
        if (dialog.ShowDialog() != true)
            return;

        var config = LibraryService.LoadConfig();
        if (!config.Any(e => string.Equals(e.Path, dialog.FolderName, StringComparison.OrdinalIgnoreCase)))
        {
            config.Add(new LibraryConfigEntry(dialog.FolderName, null));
            LibraryService.SaveConfig(config);
        }
        RefreshLibraries();
    }

    [RelayCommand]
    private void RemoveLibrary(LibraryListItem? item)
    {
        if (item is null || !item.CanRemove)
            return;
        var config = LibraryService.LoadConfig();
        config.RemoveAll(e => string.Equals(e.Path, item.Path, StringComparison.OrdinalIgnoreCase));
        LibraryService.SaveConfig(config);
        RefreshLibraries();
    }

    [RelayCommand]
    private void RenameLibrary(LibraryListItem? item)
    {
        if (item is null || item.Managed)
            return;
        var newName = Controls.PromptWindow.Show("Bibliothek umbenennen",
            $"Anzeigename für:\n{item.Path}", item.Name);
        if (newName is null)
            return;
        LibraryService.RenameLibrary(item.Path, newName);
        RefreshLibraries();
    }

    [RelayCommand]
    private void OpenLibrary(LibraryListItem? item)
    {
        if (item is null)
            return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", item.Path) { UseShellExecute = true });
        }
        catch { }
    }

    [RelayCommand]
    private async Task MigrateLibraryAsync(LibraryListItem? item)
    {
        if (item is null || _communityPath is null)
            return;

        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Ziel-Ordner für die Migration wählen" };
        if (dialog.ShowDialog() != true)
            return;

        var count = LibraryService.CountAddons(item.Path);
        var confirm = Controls.AppDialog.Confirm("Bibliothek migrieren",
            $"{count} Add-on(s) migrieren?\n\nVon: {item.Path}\nNach: {dialog.FolderName}\n\nAktive Links im Community-Ordner werden automatisch auf den neuen Ort umgebogen. Bei großen Datenmengen (laufwerksübergreifend) kann das dauern.",
            "Migrieren");
        if (!confirm)
            return;

        LibrarySummary = "Migriere Bibliothek …";
        var source = item.Path;
        var target = dialog.FolderName;
        var (ok, log) = await Task.Run(() => LibraryService.MigrateLibrary(_communityPath, source, target, _community2024Path));
        if (ok)
            Controls.AppDialog.Info("Migration abgeschlossen", string.Join("\n", log));
        else
            Controls.AppDialog.Warn("Migration mit Fehlern", string.Join("\n", log));
        RefreshLibraries();
        _ = RefreshAddonAnalysisAsync();
    }

    [RelayCommand]
    private async Task DissolveLibraryAsync(LibraryListItem? item)
    {
        if (item is null || _communityPath is null)
            return;

        var count = LibraryService.CountAddons(item.Path);
        var confirm = Controls.AppDialog.Confirm("Bibliothek auflösen",
            $"Bibliothek auflösen?\n\n{item.Path}\n\nAlle {count} Add-on(s) werden als echte Ordner in den Community-Ordner zurückverschoben — bisher INAKTIVE Add-ons werden dadurch AKTIV. Die Bibliothek verschwindet anschließend aus der Liste.",
            "Auflösen", kind: Controls.AppDialog.Kind.Warning);
        if (!confirm)
            return;

        LibrarySummary = "Löse Bibliothek auf …";
        var source = item.Path;
        var (ok, log) = await Task.Run(() => LibraryService.DissolveToCommunity(_communityPath, source, _community2024Path));
        if (ok)
            Controls.AppDialog.Info("Bibliothek aufgelöst", string.Join("\n", log));
        else
            Controls.AppDialog.Warn("Auflösen mit Fehlern", string.Join("\n", log));
        await LoadAsync();
    }

    [RelayCommand]
    private void ActivateLibraryAddon(LibraryRowItem? row) => ActivateAddonTo(row, toCommunity2024: false);

    /// <summary>Aktiviert per Junction — wahlweise im klassischen Community- oder im Community2024-Ordner.</summary>
    public void ActivateAddonTo(LibraryRowItem? row, bool toCommunity2024)
    {
        var root = toCommunity2024 ? _community2024Path : _communityPath;
        if (row is null || root is null)
            return;
        var (ok, message) = LibraryService.Activate(root, row.Addon);
        if (!ok)
            Controls.AppDialog.Warn("Aktivieren", message);
        RefreshLibraries();
        _ = RefreshAddonAnalysisAsync();
    }

    private static bool IsCommunityFixed(LibraryAddon addon)
        => !addon.IsLink &&
           (addon.Location.Equals("Community", StringComparison.OrdinalIgnoreCase) ||
            addon.Location.Equals("Community2024", StringComparison.OrdinalIgnoreCase));

    /// <summary>Community-Wurzel, in der der Aktivierungs-Link dieses Add-ons liegt.</summary>
    private string? LinkRootFor(LibraryAddon addon)
    {
        if (addon.IsLink)
            return System.IO.Path.GetDirectoryName(addon.Path);
        return addon.ActiveIn.Equals("Community2024", StringComparison.OrdinalIgnoreCase)
            ? _community2024Path
            : _communityPath;
    }

    [RelayCommand]
    private async Task DeactivateLibraryAddonAsync(LibraryRowItem? row)
    {
        if (row is null || _communityPath is null)
            return;

        if (!IsCommunityFixed(row.Addon))
        {
            // Verlinkt: nur den Link entfernen — in der Wurzel, in der er tatsächlich liegt.
            var root = LinkRootFor(row.Addon) ?? _communityPath;
            var (ok, message) = LibraryService.Deactivate(root, row.Addon.FolderName);
            if (!ok)
                Controls.AppDialog.Warn("Deaktivieren", message);
            RefreshLibraries();
            _ = RefreshAddonAnalysisAsync();
            return;
        }

        // Fest installiert: Deaktivieren = ohne Link in eine Bibliothek verschieben.
        var defaultLibrary = LibraryList
            .FirstOrDefault(l => !l.Managed && !l.Path.Contains("Program Files", StringComparison.OrdinalIgnoreCase))?.Path;
        if (defaultLibrary is null)
        {
            Controls.AppDialog.Warn("Keine Bibliothek vorhanden",
                "Zum Deaktivieren eines fest installierten Add-ons wird eine Bibliothek benötigt (die Daten müssen ja irgendwo hin). Bitte zuerst oben eine Bibliothek hinzufügen.");
            return;
        }

        var confirm = Controls.AppDialog.Confirm("Deaktivieren (fest installiert)",
            $"„{row.Display}“ deaktivieren?\n\nDas Add-on wird OHNE Link in die Bibliothek verschoben:\n{defaultLibrary}\n\nMSFS lädt es danach nicht mehr; Reaktivieren geht jederzeit per Klick.",
            "Deaktivieren");
        if (!confirm)
            return;

        await MoveAddonToTargetAsync(row, defaultLibrary, keepActive: false);
    }

    [RelayCommand]
    private async Task InstallFromArchiveAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add-on-Archiv wählen",
            Filter = "Add-on-Archive (*.zip;*.7z;*.rar)|*.zip;*.7z;*.rar|Alle Dateien|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog() != true)
            return;
        await InstallFromSourcesAsync(dialog.FileNames);
    }

    [RelayCommand]
    private async Task InstallFromFolderAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Add-on-Ordner wählen (enthält manifest.json — oder mehrere Pakete)",
            Multiselect = true,
        };
        if (dialog.ShowDialog() != true)
            return;
        await InstallFromSourcesAsync(dialog.FolderNames);
    }

    /// <summary>Gemeinsamer Installations-Flow für Dateidialog, Ordnerdialog und Drag &amp; Drop.</summary>
    public async Task InstallFromSourcesAsync(IReadOnlyList<string> sources)
    {
        if (_communityPath is null)
        {
            Controls.AppDialog.Warn("Add-on installieren", "Kein Community-Ordner gefunden — Installation nicht möglich.");
            return;
        }

        // Entpacken/Analysieren kann bei großen Archiven dauern — Overlay wie bei der System-Analyse.
        IsLoading = true;
        var preparations = new List<InstallPreparation>();
        foreach (var source in sources)
        {
            var label = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(source));
            StatusText = AddonInstaller.IsArchive(source)
                ? $"Entpacke „{label}“ …"
                : $"Analysiere „{label}“ …";
            preparations.Add(await Task.Run(() => AddonInstaller.Prepare(source)));
        }
        IsLoading = false;

        try
        {
            var errors = preparations.Where(p => p.Error is not null).Select(p => p.Error!).ToList();
            var candidates = preparations
                .SelectMany(p => p.Candidates.Select(c => (Prep: p, Candidate: c)))
                .ToList();
            // GSX-Profile über alle Quellen einsammeln, gleiche ICAOs zusammenführen.
            var gsxCandidates = preparations
                .SelectMany(p => p.GsxProfiles)
                .GroupBy(c => c.Icao, StringComparer.OrdinalIgnoreCase)
                .Select(g => new GsxProfileCandidate(g.Key, g.SelectMany(c => c.Variants).ToList()))
                .OrderBy(c => c.Icao, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (candidates.Count == 0 && gsxCandidates.Count == 0)
            {
                Controls.AppDialog.Warn("Add-on installieren",
                    string.Join("\n", errors.DefaultIfEmpty("Keine installierbaren Pakete gefunden.")));
                RefreshLibraries();
                return;
            }

            var log = new List<string>(errors);

            if (candidates.Count > 0)
            {
                var choice = Controls.InstallDialog.Show(
                    candidates.Select(c => c.Candidate).ToList(),
                    LibraryList.Select(l => (l.Path, l.Name)).ToList(),
                    hasCommunity2024: _community2024Path is not null);
                if (choice is null)
                {
                    RefreshLibraries();
                    return;
                }
                // Fest-Installationen landen wahlweise im klassischen Community- oder im Community2024-Ordner.
                var communityRoot = choice.ToCommunity2024 && _community2024Path is not null
                    ? _community2024Path
                    : _communityPath;

                IsLoading = true;
                foreach (var (prep, candidate) in candidates)
                {
                    StatusText = $"Installiere „{candidate.Title}“ …";
                    var (ok, message) = await Task.Run(() => AddonInstaller.Install(
                        candidate, communityRoot!, choice.TargetLibrary, choice.Activate,
                        copyInsteadOfMove: prep.SourceIsUserFolder, overwrite: false));

                    if (!ok && message == "EXISTS")
                    {
                        var confirm = Controls.AppDialog.Confirm("Bereits installiert",
                            $"„{candidate.FolderName}“ existiert am Ziel bereits.\n\nVorhandene Version ersetzen?",
                            "Ersetzen", "Überspringen");
                        if (confirm)
                            (ok, message) = await Task.Run(() => AddonInstaller.Install(
                                candidate, communityRoot!, choice.TargetLibrary, choice.Activate,
                                copyInsteadOfMove: prep.SourceIsUserFolder, overwrite: true));
                        else
                            message = $"„{candidate.Title}“ übersprungen (bereits vorhanden).";
                    }
                    log.Add((ok ? "✓ " : "✗ ") + message);
                }
                IsLoading = false;
            }

            if (gsxCandidates.Count > 0)
            {
                if (!GsxProfileService.IsGsxInstalled)
                {
                    log.Add("✗ GSX-Profile gefunden, aber GSX ist nicht installiert — übersprungen.");
                }
                else
                {
                    var (hasNool, hasAsVdgs) = GsxProfileService.DetectInstalledVdgs(_allPackageNames);
                    var gsxChoices = Controls.GsxInstallDialog.Show(gsxCandidates, hasNool, hasAsVdgs);
                    if (gsxChoices is not null)
                    {
                        foreach (var (candidate, variant) in gsxChoices)
                        {
                            if (variant is null)
                            {
                                log.Add($"⏭ GSX-Profil {candidate.Icao} übersprungen.");
                                continue;
                            }
                            var (ok, message) = GsxProfileService.InstallVariant(candidate.Icao, variant, replaceExisting: false);
                            if (!ok && message == "EXISTS")
                            {
                                var confirm = Controls.AppDialog.Confirm("GSX-Profil vorhanden",
                                    $"Für {candidate.Icao} existiert bereits ein GSX-Profil.\n\nErsetzen? (Die alten Dateien wandern in ein Backup.)",
                                    "Ersetzen", "Überspringen");
                                if (confirm)
                                    (ok, message) = GsxProfileService.InstallVariant(candidate.Icao, variant, replaceExisting: true);
                                else
                                    message = $"GSX-Profil {candidate.Icao} übersprungen (bereits vorhanden).";
                            }
                            log.Add((ok ? "✓ " : "✗ ") + message);
                        }
                    }
                    else if (candidates.Count == 0)
                    {
                        RefreshLibraries();
                        return;
                    }
                }
            }

            // GSX-Aircraft-Configs (Airplanes\*.cfg, z.B. Fenix-Flottenprofile)
            var aircraftConfigs = preparations
                .SelectMany(p => p.GsxAircraftConfigs)
                .GroupBy(c => c.AircraftFolder, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            if (aircraftConfigs.Count > 0)
            {
                if (!GsxProfileService.IsGsxInstalled)
                {
                    log.Add("✗ GSX-Aircraft-Configs gefunden, aber GSX ist nicht installiert — übersprungen.");
                }
                else
                {
                    var list = string.Join("\n", aircraftConfigs.Select(c => $"• {c.AircraftFolder} ({c.FileCount} Datei(en))"));
                    var confirmAircraft = Controls.AppDialog.Confirm("GSX-Aircraft-Configs gefunden",
                        $"Flugzeug-Profile für den GSX-Ordner „virtuali\\Airplanes“:\n\n{list}\n\nInstallieren? Vorhandene Dateien gleichen Namens werden ersetzt (mit Backup).",
                        "Installieren", "Überspringen");
                    if (confirmAircraft)
                        foreach (var config in aircraftConfigs)
                        {
                            var (ok, message) = GsxProfileService.InstallAircraftConfig(config);
                            log.Add((ok ? "✓ " : "✗ ") + message);
                        }
                    else
                        log.Add("⏭ GSX-Aircraft-Configs übersprungen.");
                }
            }

            if (log.Any(l => l.StartsWith("✗")))
                Controls.AppDialog.Warn("Add-on installieren", string.Join("\n", log));
            else
                Controls.AppDialog.Info("Add-on installieren", string.Join("\n", log));
        }
        finally
        {
            IsLoading = false;
            foreach (var preparation in preparations)
                AddonInstaller.Cleanup(preparation);
            RefreshLibraries();
            _ = RefreshAddonAnalysisAsync();
        }
    }

    /// <summary>
    /// Layout-Check über alle Pakete: findet sim-relevante Dateien, die nicht in der layout.json
    /// stehen (der Sim IGNORIERT sie — Livery-Klassiker) und bietet gezielt Reparatur an.
    /// Fehlende/größenabweichende Einträge sind meist Hersteller-Konfiguratoren → nur Info.
    /// </summary>
    [RelayCommand]
    private async Task CheckLayoutsAsync()
    {
        var installation = Analysis?.MsfsInstallations.FirstOrDefault(i => i.UserCfgExists);
        if (installation is null)
            return;

        IsLoading = true;
        StatusText = "Layout-Check läuft — gleiche alle Pakete gegen ihre layout.json ab …";
        var (results, managedRoots) = await Task.Run(() =>
        {
            var scan = LibraryService.Scan(installation);
            var dirs = scan.CommunityItems.Where(c => !c.IsBroken).Select(c => c.Path)
                .Concat(scan.LibraryItems.Select(l => l.Path))
                .Select(p => { try { return System.IO.Path.TrimEndingDirectorySeparator(new System.IO.DirectoryInfo(p).LinkTarget ?? p); } catch { return p; } })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var managed = scan.Libraries.Where(l => l.Managed).Select(l => l.Path).ToList();
            return (dirs.Select(Core.Msfs.LayoutService.Check).Where(r => r.HasLayout && !r.IsClean).ToList(), managed);
        });
        IsLoading = false;

        if (results.Count == 0)
        {
            Controls.AppDialog.Info("Layout-Check", "Alle Pakete sind konsistent — jede layout.json passt zum Ordnerinhalt. ✓");
            return;
        }

        bool IsManaged(string dir) => managedRoots.Any(root => dir.StartsWith(root, StringComparison.OrdinalIgnoreCase));
        var repairable = results.Where(r => r.UnlistedFiles.Count > 0 && !IsManaged(r.PackageDir)).ToList();
        var infoOnly = results.Where(r => !repairable.Contains(r)).ToList();

        var message = new System.Text.StringBuilder();
        if (repairable.Count > 0)
        {
            message.AppendLine($"REPARIERBAR — {repairable.Count} Paket(e) mit sim-relevanten Dateien, die NICHT in der layout.json stehen (der Sim ignoriert sie, z.B. manuell installierte Liveries/Modelle):");
            foreach (var result in repairable.Take(10))
                message.AppendLine($"• {System.IO.Path.GetFileName(result.PackageDir)} — {result.UnlistedFiles.Count} Datei(en), z.B. {result.UnlistedFiles[0]}");
            if (repairable.Count > 10)
                message.AppendLine($"… und {repairable.Count - 10} weitere");
            message.AppendLine();
        }
        if (infoOnly.Count > 0)
        {
            message.AppendLine($"NUR INFO — {infoOnly.Count} Paket(e) mit fehlenden/größenabweichenden Einträgen. Das ist fast immer Absicht (Hersteller-Konfiguratoren wie iniBuilds/Aerosoft, FSLTL-Installer, optionale VDGS-Module) — NICHT reparieren:");
            foreach (var result in infoOnly.Take(8))
                message.AppendLine($"• {System.IO.Path.GetFileName(result.PackageDir)} — {result.Summary}");
            if (infoOnly.Count > 8)
                message.AppendLine($"… und {infoOnly.Count - 8} weitere");
        }

        if (repairable.Count == 0)
        {
            Controls.AppDialog.Info("Layout-Check", message.ToString().TrimEnd());
            return;
        }

        var confirm = Controls.AppDialog.Confirm("Layout-Check",
            message.ToString().TrimEnd() + "\n\nDie reparierbaren Pakete jetzt fixen? (layout.json wird aus dem Ordnerinhalt neu erzeugt; deaktivierte .off-Optionen bleiben erhalten, Backups automatisch.)",
            "Reparieren", "Schließen");
        if (!confirm)
            return;

        var log = new List<string>();
        foreach (var result in repairable)
        {
            var (ok, repairMessage) = await Task.Run(() => Core.Msfs.LayoutService.Regenerate(result.PackageDir));
            log.Add((ok ? "✓ " : "✗ ") + System.IO.Path.GetFileName(result.PackageDir) + ": " + repairMessage);
        }
        Controls.AppDialog.Info("Layout-Reparatur", string.Join("\n", log));
        _ = RefreshAddonAnalysisAsync();
    }

    /// <summary>Universelles Verschieben — Ziel aus dem „Verschieben“-Menü (null = Community-Ordner,
    /// toCommunity2024 = fest in den Community2024-Ordner).</summary>
    public async Task MoveAddonToTargetAsync(LibraryRowItem row, string? targetLibrary, bool keepActive = true,
        bool toCommunity2024 = false)
    {
        if (_communityPath is null)
            return;

        LibrarySummary = $"Verschiebe „{row.Display}“ …";
        var fixedRoot = toCommunity2024 ? _community2024Path : null;
        var (ok, message) = await Task.Run(() =>
            LibraryService.MoveAddon(_communityPath, row.Addon, targetLibrary, keepActive,
                _community2024Path, fixedRoot));

        if (ok)
            Controls.AppDialog.Info("Verschoben", message);
        else
            Controls.AppDialog.Warn("Fehlgeschlagen", message);
        RefreshLibraries();
        _ = RefreshAddonAnalysisAsync();
    }
}
