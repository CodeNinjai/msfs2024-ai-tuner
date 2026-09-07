using System.Collections.ObjectModel;
using System.Diagnostics;
using AiTuner.Core.Apply;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiTuner.App.ViewModels;

/// <summary>Zeile der Flug-Modus-Dienstliste (Haken änderbar).</summary>
public sealed partial class ServiceRow : ObservableObject
{
    public required string Name { get; init; }
    public required string Display { get; init; }
    public required string Status { get; init; }
    public required bool IsRunning { get; init; }
    public required string Tooltip { get; init; }
    [ObservableProperty] private bool _isChecked;
}

/// <summary>„Flug-Modus“: Hintergrunddienste temporär pausieren, solange MSFS läuft.</summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<ServiceRow> FlightModeServices { get; } = new();
    public ObservableCollection<string> BackgroundActivity { get; } = new();

    [ObservableProperty] private string _flightModeStatus = "";
    [ObservableProperty] private bool _flightModePauseActive;

    public void RefreshFlightMode()
    {
        FlightModeServices.Clear();
        var selection = ServicePauseService.LoadSelection();
        foreach (var service in ServicePauseService.Catalog)
        {
            var status = ServicePauseService.GetStatus(service.Name);
            if (status == "Nicht vorhanden")
                continue;
            FlightModeServices.Add(new ServiceRow
            {
                Name = service.Name,
                Display = service.Display,
                Status = status,
                IsRunning = status is "Läuft" or "Startet",
                Tooltip = $"{service.Reason}\n\nWährend der Pause: {service.WhileStopped}",
                IsChecked = selection.Contains(service.Name, StringComparer.OrdinalIgnoreCase),
            });
        }
        FlightModePauseActive = ServicePauseService.IsPauseActive;
        FlightModeStatus = FlightModePauseActive
            ? "Flug-Modus AKTIV — Dienste sind pausiert; automatische Wiederherstellung beim Sim-Ende."
            : "Bereit — der Admin-Helfer stoppt die gewählten Dienste und stellt sie nach dem Sim-Ende selbstständig wieder her.";
    }

    [RelayCommand]
    private async Task StartFlightModeAsync()
    {
        var selected = FlightModeServices.Where(s => s.IsChecked).Select(s => s.Name).ToList();
        if (selected.Count == 0)
        {
            Controls.AppDialog.Warn("Flug-Modus", "Keine Dienste ausgewählt — bitte mindestens einen Dienst anhaken.");
            return;
        }
        ServicePauseService.SaveSelection(selected);

        var confirm = Controls.AppDialog.Confirm("Flug-Modus starten",
            $"{selected.Count} Dienst(e) werden jetzt pausiert (UAC-Abfrage folgt).\n\nDer Admin-Helfer läuft im Hintergrund weiter, wartet auf das Ende von MSFS und stellt danach alles automatisch wieder her — auch wenn diese App vorher geschlossen wird.",
            "Flug-Modus starten");
        if (!confirm)
            return;

        var (ok, message) = ElevationHelper.RunSelfElevatedDetached("--svc-flightmode", string.Join(",", selected));
        if (!ok)
        {
            Controls.AppDialog.Warn("Flug-Modus", message);
            return;
        }

        // Dem Helfer kurz Zeit geben, dann Status aktualisieren.
        await Task.Delay(4000);
        RefreshFlightMode();
    }

    [RelayCommand]
    private async Task RestoreServicesAsync()
    {
        var (ok, message) = ElevationHelper.RunSelfElevatedDetached("--svc-resume");
        if (!ok)
        {
            Controls.AppDialog.Warn("Dienste wiederherstellen", message);
            return;
        }
        for (var i = 0; i < 30 && ServicePauseService.IsPauseActive; i++)
            await Task.Delay(1000);
        RefreshFlightMode();
        if (!FlightModePauseActive)
            Controls.AppDialog.Info("Dienste wiederhergestellt", "Alle pausierten Dienste laufen wieder.");
    }

    /// <summary>Misst 2 Sekunden lang, welche Prozesse im Hintergrund wirklich CPU verbrauchen.</summary>
    [RelayCommand]
    private async Task SampleBackgroundActivityAsync()
    {
        BackgroundActivity.Clear();
        BackgroundActivity.Add("Messe 2 Sekunden …");

        var result = await Task.Run(() =>
        {
            var before = new Dictionary<int, (string Name, TimeSpan Cpu, long Ram)>();
            foreach (var process in Process.GetProcesses())
            {
                try { before[process.Id] = (process.ProcessName, process.TotalProcessorTime, process.WorkingSet64); }
                catch { }
                finally { process.Dispose(); }
            }

            Thread.Sleep(2000);

            var rows = new List<(string Name, double CpuPercent, long Ram)>();
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (!before.TryGetValue(process.Id, out var b) || process.ProcessName != b.Name)
                        continue;
                    var deltaMs = (process.TotalProcessorTime - b.Cpu).TotalMilliseconds;
                    var percent = deltaMs / (2000.0 * Environment.ProcessorCount) * 100.0;
                    if (percent > 0.1)
                        rows.Add((process.ProcessName, percent, process.WorkingSet64));
                }
                catch { }
                finally { process.Dispose(); }
            }
            return rows.OrderByDescending(r => r.CpuPercent).Take(8).ToList();
        });

        BackgroundActivity.Clear();
        if (result.Count == 0)
        {
            BackgroundActivity.Add("Kein nennenswerter Hintergrund-Verbrauch (< 0,1 % CPU) — alles ruhig.");
            return;
        }
        foreach (var (name, cpu, ram) in result)
            BackgroundActivity.Add($"{name} — {cpu:F1} % CPU · {ram / (1024 * 1024)} MB RAM");
    }
}
