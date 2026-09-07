using System.Collections.ObjectModel;
using System.Diagnostics;
using AiTuner.Core.Msfs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiTuner.App.ViewModels;

/// <summary>Zeile der Autostart-Liste (EXE.xml).</summary>
public sealed record AutostartRow(ExeXmlEntry Entry)
{
    public string Name => Entry.Name;
    public string Meta => Entry.CommandLine.Length > 0 ? $"{Entry.Path}   ·   {Entry.CommandLine}" : Entry.Path;
    public bool IsActive => !Entry.Disabled;
    public string StateLabel => Entry.Disabled ? "Deaktiviert" : (Entry.PathExists ? "Startet mit" : "Datei fehlt!");
    public bool IsBrokenActive => !Entry.Disabled && !Entry.PathExists;
    public string ToggleLabel => Entry.Disabled ? "Aktivieren" : "Deaktivieren";
    public string StateTooltip => Entry.Disabled
        ? "Disabled=True in der EXE.xml — der Sim startet dieses Programm nicht mit."
        : Entry.PathExists
            ? "Startet automatisch mit dem Sim (EXE.xml)."
            : "Als aktiv eingetragen, aber die Programmdatei existiert nicht mehr — Rest eines deinstallierten Add-ons. Gefahrlos deaktivierbar.";
}

/// <summary>Autostart-Verwaltung: Programme, die MSFS über die EXE.xml mitstartet.</summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<AutostartRow> AutostartRows { get; } = new();

    [ObservableProperty] private string _autostartSummary = "";
    [ObservableProperty] private bool _hasAutostart;

    public void RefreshAutostart()
    {
        AutostartRows.Clear();
        var installation = Analysis?.MsfsInstallations.FirstOrDefault(i => i.UserCfgExists);
        if (installation is null || ExeXmlService.GetPath(installation) is null)
        {
            HasAutostart = false;
            AutostartSummary = "Keine EXE.xml gefunden — es sind keine Autostart-Programme eingetragen.";
            return;
        }

        var entries = ExeXmlService.Load(installation);
        foreach (var entry in entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            AutostartRows.Add(new AutostartRow(entry));

        HasAutostart = entries.Count > 0;
        var active = entries.Count(e => !e.Disabled);
        var broken = entries.Count(e => !e.Disabled && !e.PathExists);
        AutostartSummary = $"{entries.Count} Eintrag/Einträge · {active} starten mit dem Sim"
            + (broken > 0 ? $" · ⚠ {broken} mit fehlender Programmdatei" : "")
            + $" — {ExeXmlService.GetPath(installation)}";
    }

    [RelayCommand]
    private void ToggleAutostart(AutostartRow? row)
    {
        var installation = Analysis?.MsfsInstallations.FirstOrDefault(i => i.UserCfgExists);
        if (row is null || installation is null)
            return;

        var (ok, message) = ExeXmlService.SetDisabled(installation, row.Entry.Name, disabled: !row.Entry.Disabled);
        if (ok)
            Controls.AppDialog.Info("Autostart", message);
        else
            Controls.AppDialog.Warn("Autostart", message);
        RefreshAutostart();
    }

    // ---------- Regelwerk-Export/-Import (Community-Regelsets) ----------

    [ObservableProperty] private bool _hasRulesOverride = Core.Rules.RuleEngine.HasOverride;

    public string RulesetStatus => HasRulesOverride
        ? "Importiertes Regelwerk aktiv"
        : "Eingebautes Regelwerk aktiv";

    partial void OnHasRulesOverrideChanged(bool value) => OnPropertyChanged(nameof(RulesetStatus));

    [RelayCommand]
    private void ExportRules()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Regelwerk exportieren",
            FileName = $"aituner-rules-{DateTime.Now:yyyy-MM-dd}.json",
            Filter = "Regelwerk (*.json)|*.json",
        };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            System.IO.File.WriteAllText(dialog.FileName, Core.Rules.RuleEngine.ActiveRulesJson());
            Controls.AppDialog.Info("Regelwerk exportiert", $"Gespeichert nach:\n{dialog.FileName}");
        }
        catch (Exception ex)
        {
            Controls.AppDialog.Warn("Export fehlgeschlagen", ex.Message);
        }
    }

    [RelayCommand]
    private void ImportRules()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Regelwerk importieren",
            Filter = "Regelwerk (*.json)|*.json|Alle Dateien|*.*",
        };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            var json = System.IO.File.ReadAllText(dialog.FileName);
            var (ok, count, message) = Core.Rules.RuleEngine.ValidateRulesJson(json);
            if (!ok)
            {
                Controls.AppDialog.Warn("Import abgelehnt", message);
                return;
            }
            var confirm = Controls.AppDialog.Confirm("Regelwerk importieren",
                $"{count} Regeln aus:\n{dialog.FileName}\n\nDas importierte Regelwerk ersetzt das eingebaute, bis es zurückgesetzt wird. Empfehlungen werden sofort neu berechnet.",
                "Importieren");
            if (!confirm)
                return;
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Core.Rules.RuleEngine.OverridePath)!);
            System.IO.File.WriteAllText(Core.Rules.RuleEngine.OverridePath, json);
            HasRulesOverride = true;
            if (Analysis is not null)
                BuildRecommendations(Analysis);
        }
        catch (Exception ex)
        {
            Controls.AppDialog.Warn("Import fehlgeschlagen", ex.Message);
        }
    }

    [RelayCommand]
    private void ResetRules()
    {
        try
        {
            if (Core.Rules.RuleEngine.HasOverride)
                System.IO.File.Delete(Core.Rules.RuleEngine.OverridePath);
            HasRulesOverride = false;
            if (Analysis is not null)
                BuildRecommendations(Analysis);
        }
        catch (Exception ex)
        {
            Controls.AppDialog.Warn("Zurücksetzen fehlgeschlagen", ex.Message);
        }
    }

    [RelayCommand]
    private void OpenAutostartFolder(AutostartRow? row)
    {
        if (row is null || row.Entry.Path.Length == 0)
            return;
        try
        {
            var argument = row.Entry.PathExists ? $"/select,\"{row.Entry.Path}\"" : $"\"{System.IO.Path.GetDirectoryName(row.Entry.Path)}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", argument) { UseShellExecute = true });
        }
        catch { }
    }
}
