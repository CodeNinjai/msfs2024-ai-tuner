using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using AiTuner.Core.Apply;
using AiTuner.Core.Benchmark;
using AiTuner.Core.Profiles;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiTuner.App.ViewModels;

/// <summary>Stage-2 actions: apply, backups/profiles, cache cleanup, benchmark, CCD pinning.</summary>
public sealed partial class MainViewModel
{
    [ObservableProperty] private string _newProfileName = "";
    [ObservableProperty] private bool _msfsPendingRestart;
    [ObservableProperty] private string _recCount = "0";
    [ObservableProperty] private string _recHighText = "0 hoch";
    [ObservableProperty] private string _recMediumText = "0 mittel";
    [ObservableProperty] private string _recInfoText = "0 Hinweise";

    [RelayCommand]
    private Task RefreshAnalysisAsync() => LoadAsync();
    [ObservableProperty] private string _presentMonStatus = "";
    [ObservableProperty] private string _benchmarkStatus = "Bereit — Benchmark starten, während der Sim läuft.";
    [ObservableProperty] private string _benchmarkLabel = "";
    [ObservableProperty] private int _selectedDuration = 60;
    [ObservableProperty] private bool _isBenchmarkRunning;

    public int[] Durations { get; } = [30, 60, 120];

    public ObservableCollection<ProfileItem> Profiles { get; } = new();
    public ObservableCollection<BenchmarkRow> BenchmarkResults { get; } = new();

    private bool _sessionBackupDone;
    private string? _lastAppliedProfileName;

    // ---------- Empfehlungen anwenden ----------

    [RelayCommand]
    private async Task ApplyRecommendationAsync(RecommendationRow? row)
    {
        if (row?.ActionJson is null || Analysis is null)
            return;

        var confirm = Controls.AppDialog.Confirm("Empfehlung anwenden",
            $"{row.Title}\n\n{row.Current}  →  {row.Recommended}\n\nVor der ersten Änderung wird automatisch ein Backup angelegt. Jetzt anwenden?",
            "Anwenden");
        if (!confirm)
            return;

        EnsureAutoBackup();

        var outcome = await Task.Run(() => ActionExecutor.Execute(row.ActionJson, Analysis));

        var suffix = outcome.NeedsReboot ? "\n\nHinweis: Wirksam nach einem Windows-Neustart."
            : outcome.NeedsSimRestart ? "\n\nHinweis: Wirksam beim nächsten Sim-Start."
            : "";
        if (outcome.Ok)
            Controls.AppDialog.Info("Angewendet", outcome.Message + suffix);
        else
            Controls.AppDialog.Warn("Fehlgeschlagen", outcome.Message + suffix);

        if (outcome.Ok)
        {
            _lastAppliedProfileName = null; // Zustand entspricht keinem gespeicherten Profil mehr.
            await LoadAsync();
        }
    }

    /// <summary>Write path for the settings editor rows (enum change / number apply).</summary>
    private async Task ApplySettingAsync(EditableSettingRow row, string value)
    {
        if (Analysis is null)
            return;

        EnsureAutoBackup();

        var outcome = await Task.Run(() => Core.Settings.SettingWriter.Write(row.Definition, value, Analysis));
        if (!outcome.Ok)
        {
            row.RevertSelection();
            Controls.AppDialog.Warn(row.Name, outcome.Message);
            return;
        }
        row.ConfirmSelection();
        _lastAppliedProfileName = null; // Zustand entspricht keinem gespeicherten Profil mehr.

        if (outcome.NeedsSimRestart)
        {
            _cfgCache.Clear();
            MsfsPendingRestart = true;
            RebuildMsfsRows(); // Abhängigkeits-Sperren live nachziehen (z.B. Enabled-Schalter umgelegt)
        }
        if (outcome.NeedsReboot)
            Controls.AppDialog.Info(row.Name, outcome.Message + "\n\nWirksam nach einem Windows-Neustart.");
    }

    private void EnsureAutoBackup()
    {
        if (_sessionBackupDone || Analysis is null)
            return;
        try
        {
            ProfileService.Capture(Analysis, $"Auto-Backup {DateTime.Now:yyyy-MM-dd HH-mm-ss}", isBackup: true);
            _sessionBackupDone = true;
        }
        catch (Exception ex)
        {
            Controls.AppDialog.Warn("Backup", "Auto-Backup fehlgeschlagen: " + ex.Message);
        }
    }

    // ---------- Caches ----------

    [RelayCommand]
    private async Task ClearCacheAsync(CacheRow? row)
    {
        if (row is null || !row.CanClear)
            return;

        var confirm = Controls.AppDialog.Confirm("Cache leeren",
            $"{row.Name} leeren?\n\n{row.Path}\n\nShader- und Rolling-Caches werden danach automatisch neu aufgebaut (kurzzeitig mehr Ruckler bzw. Streaming).",
            "Leeren");
        if (!confirm)
            return;

        var (ok, message) = await Task.Run(() =>
            row.IsFile ? CacheCleaner.DeleteFile(row.Path) : CacheCleaner.ClearDirectory(row.Path));

        if (ok)
            Controls.AppDialog.Info("Cache geleert", message);
        else
            Controls.AppDialog.Warn("Fehlgeschlagen", message);
        if (ok)
            await LoadAsync();
    }

    // ---------- Profile & Backups ----------

    public void RefreshProfiles()
    {
        var selectedExperiment = SelectedExperimentProfile?.Name;
        Profiles.Clear();
        ExperimentProfiles.Clear();
        foreach (var profile in ProfileService.List())
        {
            var item = new ProfileItem(
                profile.Name,
                profile.CreatedAt.ToString("dd.MM.yyyy HH:mm"),
                profile.IsBackup ? "Backup" : "Profil",
                profile.IsBackup);
            Profiles.Add(item);
            if (!profile.IsBackup)
                ExperimentProfiles.Add(item);
        }
        if (selectedExperiment is not null)
            SelectedExperimentProfile = ExperimentProfiles.FirstOrDefault(p => p.Name == selectedExperiment);
    }

    [RelayCommand]
    private void CreateBackup()
    {
        if (Analysis is null)
            return;
        try
        {
            var profile = ProfileService.Capture(Analysis, $"Backup {DateTime.Now:yyyy-MM-dd HH-mm-ss}", isBackup: true);
            RefreshProfiles();
            Controls.AppDialog.Info("Backup erstellt",
                $"Backup „{profile.Name}“ angelegt (Windows-Werte, Energieplan, UserCfg.opt, NVIDIA-Profil).");
        }
        catch (Exception ex)
        {
            Controls.AppDialog.Warn("Backup fehlgeschlagen", ex.Message);
        }
    }

    [RelayCommand]
    private void SaveProfile()
    {
        if (Analysis is null)
            return;
        var name = NewProfileName.Trim();
        if (name.Length == 0)
        {
            Controls.AppDialog.Warn("Profil speichern", "Bitte zuerst einen Profilnamen eingeben.");
            return;
        }
        try
        {
            ProfileService.Capture(Analysis, name, isBackup: false);
            NewProfileName = "";
            RefreshProfiles();
        }
        catch (Exception ex)
        {
            Controls.AppDialog.Warn("Profil speichern fehlgeschlagen", ex.Message);
        }
    }

    [RelayCommand]
    private async Task ApplyProfileAsync(ProfileItem? item)
    {
        if (item is null || Analysis is null)
            return;

        var confirm = Controls.AppDialog.Confirm("Profil anwenden",
            $"„{item.Name}“ ({item.TypeLabel} vom {item.CreatedText}) jetzt anwenden?\n\nStellt Windows-Werte, Energieplan, UserCfg.opt und NVIDIA-Profil aus dem Stand des Profils wieder her. Der Sim darf dabei nicht laufen.",
            "Anwenden");
        if (!confirm)
            return;

        EnsureAutoBackup();

        var profile = ProfileService.List().FirstOrDefault(p => p.Name == item.Name);
        if (profile is null)
        {
            Controls.AppDialog.Warn("Fehler", "Profil nicht mehr vorhanden.");
            return;
        }

        var (allOk, log) = await Task.Run(() => ProfileService.Apply(profile, Analysis));
        if (allOk)
            _lastAppliedProfileName = profile.Name;
        if (allOk)
            Controls.AppDialog.Info("Profil angewendet", string.Join("\n", log));
        else
            Controls.AppDialog.Warn("Teilweise fehlgeschlagen", string.Join("\n", log));
        await LoadAsync();
    }

    [RelayCommand]
    private void DeleteProfile(ProfileItem? item)
    {
        if (item is null)
            return;
        var confirm = Controls.AppDialog.Confirm("Löschen",
            $"„{item.Name}“ endgültig löschen?", "Löschen", kind: Controls.AppDialog.Kind.Warning);
        if (!confirm)
            return;
        ProfileService.Delete(item.Name);
        RefreshProfiles();
    }

    [RelayCommand]
    private void OpenProfilesFolder()
    {
        try
        {
            Directory.CreateDirectory(ProfileService.RootDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", ProfileService.RootDirectory) { UseShellExecute = true });
        }
        catch { }
    }

    // ---------- Benchmark & Sim-Werkzeuge ----------

    public void RefreshBenchmarkState()
    {
        PresentMonStatus = PresentMonService.IsAvailable
            ? "PresentMon bereit."
            : "PresentMon ist noch nicht installiert (einmaliger Download, open source von Intel).";

        BenchmarkResults.Clear();
        var results = PresentMonService.LoadResults();
        var baseline = results.FirstOrDefault(r => r.IsBaseline);

        foreach (var result in results)
        {
            string deltaFps = "", deltaLow = "";
            bool fpsGood = true, lowGood = true;
            if (baseline is not null && !result.IsBaseline && baseline.AvgFps > 0 && baseline.OnePercentLowFps > 0)
            {
                var dFps = (result.AvgFps - baseline.AvgFps) / baseline.AvgFps * 100;
                var dLow = (result.OnePercentLowFps - baseline.OnePercentLowFps) / baseline.OnePercentLowFps * 100;
                deltaFps = $"{dFps:+0.0;-0.0} %";
                deltaLow = $"{dLow:+0.0;-0.0} %";
                fpsGood = dFps >= 0;
                lowGood = dLow >= 0;
            }

            BenchmarkResults.Add(new BenchmarkRow(
                result,
                result.Label,
                result.ProfileNote ?? "—",
                result.At.ToString("dd.MM. HH:mm"),
                result.AvgFps.ToString("0.0"),
                result.OnePercentLowFps.ToString("0.0"),
                deltaFps,
                deltaLow,
                fpsGood,
                lowGood,
                result.IsBaseline,
                $"Dauer {result.DurationSeconds} s · {result.FrameCount} Frames · Ø {result.AvgFrameTimeMs} ms · Max {result.MaxFrameTimeMs} ms"));
        }
    }

    /// <summary>Warnt, wenn die neue Messung erkennbar NICHT zur Baseline passt (Ort/Flugzeug/Phase).</summary>
    private static string BuildComparabilityWarning(Core.Sim.SimSnapshot? snapshot)
    {
        if (snapshot is null)
            return "";
        var baseline = PresentMonService.LoadResults().FirstOrDefault(r => r.IsBaseline);
        if (baseline?.Latitude is not { } baselineLat || baseline.Longitude is not { } baselineLon)
            return "";

        var notes = new List<string>();
        var distanceKm = snapshot.DistanceKmTo(baselineLat, baselineLon);
        if (distanceKm > 15)
            notes.Add($"{distanceKm:0} km von der Baseline-Position entfernt");
        if (!string.IsNullOrEmpty(baseline.Aircraft)
            && !string.Equals(baseline.Aircraft, snapshot.AircraftTitle, StringComparison.OrdinalIgnoreCase))
            notes.Add($"anderes Flugzeug ({snapshot.AircraftTitle} statt {baseline.Aircraft})");
        if (baseline.OnGround is { } baselineGround && baselineGround != snapshot.OnGround)
            notes.Add(snapshot.OnGround ? "am Boden statt airborne" : "airborne statt am Boden");

        return notes.Count == 0 ? "" : $"  ⚠ Eingeschränkt vergleichbar: {string.Join(", ", notes)}.";
    }

    [RelayCommand]
    private void SetBaseline(BenchmarkRow? row)
    {
        if (row is null)
            return;
        var results = PresentMonService.LoadResults();
        foreach (var result in results)
            result.IsBaseline = result.At == row.Result.At && result.Label == row.Result.Label && !row.IsBaseline;
        PresentMonService.SaveAll(results);
        RefreshBenchmarkState();
    }

    [RelayCommand]
    private void DeleteBenchmark(BenchmarkRow? row)
    {
        if (row is null)
            return;
        var results = PresentMonService.LoadResults();
        results.RemoveAll(r => r.At == row.Result.At && r.Label == row.Result.Label);
        PresentMonService.SaveAll(results);
        RefreshBenchmarkState();
    }

    [RelayCommand]
    private async Task DownloadPresentMonAsync()
    {
        PresentMonStatus = "PresentMon wird heruntergeladen …";
        var (ok, message) = await PresentMonService.DownloadAsync();
        PresentMonStatus = message;
        if (ok)
            PresentMonStatus = "PresentMon bereit.";
    }

    [RelayCommand]
    private async Task RunBenchmarkAsync()
    {
        if (IsBenchmarkRunning)
            return;

        var processName = PresentMonService.RunningSimProcessName();
        if (processName is null)
        {
            BenchmarkStatus = "MSFS läuft nicht — bitte den Sim starten und eine reproduzierbare Situation wählen (z.B. Parkposition am selben Airport).";
            return;
        }
        if (!PresentMonService.IsAvailable)
        {
            BenchmarkStatus = "PresentMon fehlt — bitte zuerst herunterladen.";
            return;
        }

        var label = BenchmarkLabel.Trim();
        if (label.Length == 0)
            label = $"Messung {DateTime.Now:HH:mm}";

        IsBenchmarkRunning = true;
        BenchmarkStatus = $"Messung läuft ({SelectedDuration} s, Prozess {processName}) — UAC-Abfrage bestätigen …";
        try
        {
            // Sim-Kontext (Position/Flugzeug/Phase) VOR der Messung einfangen — macht Messungen vergleichbar.
            var snapshot = await Task.Run(() =>
            {
                using var sim = new Core.Sim.SimConnectService();
                return sim.Poll();
            });

            var duration = SelectedDuration;
            var (ok, message, result) = await Task.Run(() =>
                PresentMonService.RunCapture(processName, duration, label));

            if (ok && result is not null)
            {
                result.ProfileNote = _lastAppliedProfileName;
                if (snapshot is not null)
                {
                    result.Aircraft = snapshot.AircraftTitle;
                    result.Latitude = snapshot.Latitude;
                    result.Longitude = snapshot.Longitude;
                    result.OnGround = snapshot.OnGround;
                }
                PresentMonService.SaveResult(result);
                RefreshBenchmarkState();
                var contextNote = snapshot is null ? "" : $" · {snapshot.PhaseText}";
                var comparabilityWarning = BuildComparabilityWarning(snapshot);
                BenchmarkStatus = $"Fertig: Ø {result.AvgFps} FPS · 1% Low {result.OnePercentLowFps} FPS · {result.FrameCount} Frames{contextNote}.{comparabilityWarning}";
            }
            else
            {
                BenchmarkStatus = "Fehlgeschlagen: " + message;
            }
        }
        finally
        {
            IsBenchmarkRunning = false;
        }
    }

    [RelayCommand]
    private async Task QuerySimStatusAsync()
    {
        BenchmarkStatus = "Frage SimConnect ab …";
        var snapshot = await Task.Run(() =>
        {
            using var sim = new Core.Sim.SimConnectService();
            return sim.Poll(3000);
        });
        BenchmarkStatus = snapshot is null
            ? "SimConnect: keine Verbindung — läuft der Sim (und ist ein Flug geladen)?"
            : $"SimConnect ✓ {snapshot.AircraftTitle} · {snapshot.PositionText} · {snapshot.PhaseText}";
    }

    [RelayCommand]
    private void PinAffinity()
    {
        // Hybrid-CPU (Intel P/E) → P-Kerne; Dual-CCD X3D → V-Cache-CCD; sonst erste Kernhälfte.
        var cpu = Analysis?.Hardware.Cpu;
        var dualCcdX3d = cpu is not null && cpu.Manufacturer == "AMD" && cpu.L3CacheKb >= 96 * 1024 && cpu.Cores > 8;
        var (ok, message) = AffinityService.PinSimToFastCores(dualCcdX3d);
        if (ok)
            Controls.AppDialog.Info("Kern-Pinning", message);
        else
            Controls.AppDialog.Warn("Kern-Pinning fehlgeschlagen", message);
    }

    [RelayCommand]
    private void ResetAffinity()
    {
        var (ok, message) = AffinityService.ResetSimAffinity();
        if (ok)
            Controls.AppDialog.Info("Kern-Pinning", message);
        else
            Controls.AppDialog.Warn("Kern-Pinning", message);
    }
}
