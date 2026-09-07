using System.Collections.ObjectModel;
using AiTuner.Core.Msfs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiTuner.App.ViewModels;

/// <summary>Zeile der Content.xml-Liste.</summary>
public sealed record ContentRow(ContentPackage Package)
{
    public string Name => Package.PackageName;
    public string SourceLabel => Package.SourceLabel;
    public bool IsActive => Package.IsActive;
    public bool IsSystemDisabled => Package.IsSystemDisabled;
    public string StateLabel => Package.IsActive ? "Aktiv" : Package.IsSystemDisabled ? "Vom Sim stillgelegt" : "Deaktiviert";
    public string ToggleLabel => Package.IsActive ? "Deaktivieren" : "Aktivieren";
    public bool CanToggle => !Package.IsSystemDisabled;
    public string StateTooltip => Package.IsActive
        ? "Activated — der Sim lädt dieses Paket."
        : Package.IsSystemDisabled
            ? "SystemDisabled — vom Sim stillgelegt (meist veraltetes Paket). Im Sim-Content-Manager klären."
            : "UserDisabled — im Content-Manager abgewählt; der Sim lädt es nicht (zählt daher auch nicht als ICAO-Konflikt).";
}

/// <summary>Content.xml: Paket-Aktivierung des Content-Managers — durchsuchen und schalten.</summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<ContentRow> ContentRows { get; } = new();

    [ObservableProperty] private string _contentFilter = "";
    [ObservableProperty] private string _contentSummary = "";

    private List<ContentPackage> _contentPackages = new();

    partial void OnContentFilterChanged(string value) => RefreshContentRows();

    public void RefreshContent()
    {
        var installation = Analysis?.MsfsInstallations.FirstOrDefault(i => i.UserCfgExists);
        _contentPackages = installation is null ? new() : ContentXmlService.Load(installation);
        RefreshContentRows();
    }

    private void RefreshContentRows()
    {
        ContentRows.Clear();
        if (_contentPackages.Count == 0)
        {
            ContentSummary = "Keine Content.xml gefunden.";
            return;
        }

        var filter = ContentFilter.Trim();
        var userDisabled = _contentPackages.Count(p => p.IsUserDisabled);
        var systemDisabled = _contentPackages.Count(p => p.IsSystemDisabled);

        IEnumerable<ContentPackage> shown;
        if (filter.Length == 0)
        {
            // Ohne Suchbegriff: nur die Deaktivierten — Official-Pakete, die der SIM selbst
            // stillgelegt hat, sind reines Rauschen (nicht schaltbar) und bleiben ausgeblendet.
            shown = _contentPackages.Where(p => !p.IsActive && !(p.IsSystemDisabled && !p.IsCommunity));
            ContentSummary = $"{_contentPackages.Count} Pakete · {userDisabled} von dir deaktiviert · {systemDisabled} vom Sim stillgelegt (Official ausgeblendet, über Suche erreichbar) — Suchfeld filtert alle Pakete (z.B. ICAO).";
        }
        else
        {
            shown = _contentPackages.Where(p =>
                p.PackageName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || p.RawName.Contains(filter, StringComparison.OrdinalIgnoreCase));
            ContentSummary = $"Filter „{filter}“: {shown.Count()} Treffer von {_contentPackages.Count} Paketen";
        }

        foreach (var package in shown.OrderBy(p => p.PackageName, StringComparer.OrdinalIgnoreCase).Take(200))
            ContentRows.Add(new ContentRow(package));
        if (shown.Count() > 200)
            ContentSummary += " (Anzeige auf 200 begrenzt — Filter verfeinern)";
    }

    [RelayCommand]
    private void ToggleContent(ContentRow? row)
    {
        var installation = Analysis?.MsfsInstallations.FirstOrDefault(i => i.UserCfgExists);
        if (row is null || installation is null || !row.CanToggle)
            return;

        // Official-Pakete abschalten kann Sim-Inhalte lahmlegen — bewusst bestätigen lassen.
        if (!row.Package.IsCommunity && row.IsActive)
        {
            var confirm = Controls.AppDialog.Confirm("Official-Paket deaktivieren?",
                $"„{row.Name}“ ist ein {row.SourceLabel}-Paket des Sims. Das Deaktivieren kann Standard-Inhalte (Flugzeuge, Airports, Missionen) entfernen.\n\nTrotzdem deaktivieren?",
                "Deaktivieren", kind: Controls.AppDialog.Kind.Warning);
            if (!confirm)
                return;
        }

        var (ok, message) = ContentXmlService.SetActive(installation, row.Package.RawName, activate: !row.IsActive);
        if (ok)
            Controls.AppDialog.Info("Content.xml", message);
        else
            Controls.AppDialog.Warn("Content.xml", message);

        RefreshContent();
        _ = RefreshAddonAnalysisAsync(); // ICAO-/Konflikt-Checks an den neuen Ladezustand anpassen
    }
}
