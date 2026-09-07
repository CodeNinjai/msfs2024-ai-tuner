using System.Collections.ObjectModel;
using System.Windows;
using AiTuner.Core.Apply;
using AiTuner.Core.Benchmark;
using AiTuner.Core.Profiles;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiTuner.App.ViewModels;

public sealed record TuningLogEntry(string Title, string DeltaText, string DecisionText, bool Kept, bool DeltaGood);

/// <summary>
/// Geführter Tuning-Assistent. Experimente sind PROFILE: aus Empfehlungen automatisch
/// erzeugt (offline gepatcht) oder eigene gespeicherte Stände. Rollback = Baseline-Profil anwenden.
/// </summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<RecommendationRow> TuningCandidates { get; } = new();
    public ObservableCollection<ProfileItem> ExperimentProfiles { get; } = new();
    public ObservableCollection<TuningLogEntry> TuningLog { get; } = new();

    [ObservableProperty] private RecommendationRow? _selectedCandidate;
    [ObservableProperty] private ProfileItem? _selectedExperimentProfile;
    [ObservableProperty] private string _tuningStatus = "Bereit. Starte mit Schritt 1 — der Checkliste.";
    [ObservableProperty] private string _tuningBaselineText = "Noch keine Baseline aufgenommen.";
    [ObservableProperty] private string _tuningMeasureText = "";
    [ObservableProperty] private bool _tuningBaselineDone;
    [ObservableProperty] private bool _tuningApplied;
    [ObservableProperty] private bool _tuningMeasured;
    [ObservableProperty] private bool _tuningBusy;
    [ObservableProperty] private string _tuningFinalName = "";
    [ObservableProperty] private string _checkPresentMon = "";
    [ObservableProperty] private string _checkSim = "";
    [ObservableProperty] private string _checkDynamics = "";
    [ObservableProperty] private string _tuningNextLabel = "—";
    [ObservableProperty] private bool _tuningNextEnabled;
    [ObservableProperty] private string _tuningStepHint = "";

    private BenchmarkResult? _tuningBaseline;
    private BenchmarkResult? _tuningLastMeasure;
    private string? _tuningBaselineProfileName;
    private System.Windows.Threading.DispatcherTimer? _tuningWatch;

    public void StartTuningWatcher()
    {
        RefreshTuningChecklist();
        UpdateTuningStep();
        if (_tuningWatch is null)
        {
            _tuningWatch = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _tuningWatch.Tick += (_, _) =>
            {
                RefreshTuningChecklist();
                UpdateTuningStep();
            };
        }
        _tuningWatch.Start();
    }

    public void StopTuningWatcher() => _tuningWatch?.Stop();

    public void RefreshTuningChecklist()
    {
        CheckPresentMon = PresentMonService.IsAvailable
            ? "✓ PresentMon bereit"
            : "✗ PresentMon fehlt — auf der Benchmark-Seite herunterladen";
        CheckSim = MsfsApplier.IsSimRunning()
            ? "✓ MSFS läuft — reproduzierbare Situation laden (gleicher Airport, Parkposition, Wetter)"
            : "✗ MSFS läuft nicht — für Messungen starten";
        var dynamics = Analysis is null
            ? null
            : Core.Rules.FactBag.From(Analysis).Get("msfs.cfg.video.dynamicsettings");
        CheckDynamics = dynamics == "0"
            ? "✓ Dynamische Einstellungen aus (Messungen sind vergleichbar)"
            : "✗ Dynamische Einstellungen AN — auf der MSFS-Seite ausschalten, sonst verfälscht der Sim jede Messung";
    }

    [RelayCommand]
    private void RefreshTuningCheck() => RefreshTuningChecklist();

    private void UpdateTuningStep()
    {
        if (TuningBusy)
        {
            TuningNextEnabled = false;
            return;
        }
        if (!TuningBaselineDone)
        {
            TuningStepHint = "Zuerst in Schritt 2 die Baseline aufnehmen.";
            TuningNextLabel = "—";
            TuningNextEnabled = false;
            return;
        }
        if (SelectedExperimentProfile is null)
        {
            TuningStepHint = "Kein Experiment-Profil gewählt — oben eins auswählen oder aus einer Empfehlung erzeugen.";
            TuningNextLabel = "—";
            TuningNextEnabled = false;
            return;
        }

        var simRunning = MsfsApplier.IsSimRunning();
        if (!TuningApplied)
        {
            if (simRunning)
            {
                TuningStepHint = $"Experiment „{SelectedExperimentProfile.Name}“ gewählt. Nächster Handgriff: MSFS beenden — ich erkenne das automatisch.";
                TuningNextLabel = "Warte auf Sim-Ende …";
                TuningNextEnabled = false;
            }
            else
            {
                TuningStepHint = $"MSFS ist zu — Profil „{SelectedExperimentProfile.Name}“ kann angewendet werden. (Rollback ist jederzeit die Baseline.)";
                TuningNextLabel = "Experiment-Profil anwenden";
                TuningNextEnabled = true;
            }
        }
        else if (!TuningMeasured)
        {
            if (!simRunning)
            {
                TuningStepHint = "Angewendet ✓ — jetzt MSFS starten und exakt dieselbe Messsituation laden. Ich warte …";
                TuningNextLabel = "Warte auf MSFS-Start …";
                TuningNextEnabled = false;
            }
            else
            {
                TuningStepHint = "MSFS läuft — sobald die Messsituation geladen ist, Messung starten.";
                TuningNextLabel = $"Jetzt messen ({SelectedDuration} s)";
                TuningNextEnabled = true;
            }
        }
        else
        {
            TuningStepHint = "Messung fertig — unten entscheiden: Behalten oder zurück zur Baseline.";
            TuningNextLabel = "—";
            TuningNextEnabled = false;
        }
    }

    [RelayCommand]
    private async Task TuningNextAsync()
    {
        if (!TuningApplied)
            await ApplyExperimentAsync();
        else if (!TuningMeasured)
            await MeasureExperimentAsync();
        UpdateTuningStep();
    }

    partial void OnSelectedExperimentProfileChanged(ProfileItem? value) => UpdateTuningStep();

    // ---------- Baseline ----------

    [RelayCommand]
    private async Task RunTuningBaselineAsync()
    {
        if (TuningBusy || Analysis is null)
            return;
        RefreshTuningChecklist();
        if (!PresentMonService.IsAvailable)
        {
            TuningStatus = "PresentMon fehlt — zuerst auf der Benchmark-Seite herunterladen.";
            return;
        }
        var processName = PresentMonService.RunningSimProcessName();
        if (processName is null)
        {
            TuningStatus = "MSFS läuft nicht — Sim starten und die Messsituation laden.";
            return;
        }

        TuningBusy = true;
        TuningStatus = $"Sichere Baseline-Profil und messe Referenz ({SelectedDuration} s) — UAC bestätigen …";
        try
        {
            var name = $"Tuning-Basis {DateTime.Now:yyyy-MM-dd HH-mm}";
            await Task.Run(() => ProfileService.Capture(Analysis, name, isBackup: false));
            _tuningBaselineProfileName = name;

            var duration = SelectedDuration;
            var (ok, message, result) = await Task.Run(() =>
                PresentMonService.RunCapture(processName, duration, "Tuning-Baseline"));
            if (!ok || result is null)
            {
                TuningStatus = "Baseline-Messung fehlgeschlagen: " + message;
                return;
            }

            result.ProfileNote = name;
            MarkAsBaseline(result);
            _tuningBaseline = result;
            TuningBaselineDone = true;
            TuningBaselineText = $"Baseline: Ø {result.AvgFps} FPS · 1% Low {result.OnePercentLowFps} FPS — Profil „{name}“ gesichert (= Rollback-Punkt).";
            TuningStatus = "Baseline steht — weiter mit Schritt 3.";
            RefreshBenchmarkState();
            RefreshProfiles();
            UpdateTuningStep();
        }
        finally
        {
            TuningBusy = false;
        }
    }

    private static void MarkAsBaseline(BenchmarkResult result)
    {
        var results = PresentMonService.LoadResults();
        foreach (var entry in results)
            entry.IsBaseline = false;
        result.IsBaseline = true;
        results.Insert(0, result);
        PresentMonService.SaveAll(results);
    }

    // ---------- Experiment-Profile erzeugen ----------

    [RelayCommand]
    private async Task CreateExperimentFromCandidateAsync()
    {
        if (Analysis is null || SelectedCandidate?.ActionJson is null)
            return;

        var shortTitle = SelectedCandidate.Title.Replace("[", "").Replace("]", "");
        if (shortTitle.Length > 40)
            shortTitle = shortTitle[..40].Trim();
        var name = $"Exp {DateTime.Now:HH-mm} {shortTitle}";
        var candidate = SelectedCandidate;

        TuningStatus = $"Erzeuge Experiment-Profil „{name}“ (aktueller Stand + Änderung, offline) …";
        await Task.Run(() => ProfileService.CaptureExperiment(Analysis, name, candidate.ActionJson!));
        RefreshProfiles();
        SelectedExperimentProfile = ExperimentProfiles.FirstOrDefault(p => p.Name == name);
        TuningStatus = $"Experiment-Profil „{name}“ erzeugt und ausgewählt.";
    }

    [RelayCommand]
    private async Task SaveCurrentAsExperimentAsync()
    {
        if (Analysis is null)
            return;
        var name = $"Exp {DateTime.Now:HH-mm} Eigene Settings";
        await Task.Run(() => ProfileService.Capture(Analysis, name, isBackup: false));
        RefreshProfiles();
        SelectedExperimentProfile = ExperimentProfiles.FirstOrDefault(p => p.Name == name);
        TuningStatus = $"Aktueller Stand als „{name}“ gesichert und ausgewählt — Settings jetzt frei ändern und erneut sichern, oder direkt testen.";
    }

    // ---------- Anwenden / Messen / Entscheiden ----------

    private async Task ApplyExperimentAsync()
    {
        if (TuningBusy || Analysis is null || SelectedExperimentProfile is null)
            return;
        if (MsfsApplier.IsSimRunning())
            return;

        TuningBusy = true;
        try
        {
            var profile = ProfileService.List().FirstOrDefault(p => p.Name == SelectedExperimentProfile.Name);
            if (profile is null)
            {
                TuningStatus = "Profil nicht mehr vorhanden.";
                return;
            }
            TuningStatus = $"Wende „{profile.Name}“ an …";
            var (ok, log) = await Task.Run(() => ProfileService.Apply(profile, Analysis));
            if (!ok)
            {
                Controls.AppDialog.Warn("Anwenden mit Fehlern", string.Join("\n", log));
            }
            _lastAppliedProfileName = profile.Name;
            TuningApplied = true;
            TuningMeasured = false;
            TuningMeasureText = "";
        }
        finally
        {
            TuningBusy = false;
        }
    }

    private async Task MeasureExperimentAsync()
    {
        if (TuningBusy || _tuningBaseline is null || SelectedExperimentProfile is null)
            return;
        var processName = PresentMonService.RunningSimProcessName();
        if (processName is null)
            return;

        TuningBusy = true;
        TuningStatus = $"Messe Experiment ({SelectedDuration} s) — UAC bestätigen …";
        try
        {
            var duration = SelectedDuration;
            var label = SelectedExperimentProfile.Name;
            var (ok, message, result) = await Task.Run(() =>
                PresentMonService.RunCapture(processName, duration, label));
            if (!ok || result is null)
            {
                TuningStatus = "Messung fehlgeschlagen: " + message;
                return;
            }

            result.ProfileNote = label;
            PresentMonService.SaveResult(result);
            _tuningLastMeasure = result;
            TuningMeasured = true;

            var deltaFps = (result.AvgFps - _tuningBaseline.AvgFps) / _tuningBaseline.AvgFps * 100;
            var deltaLow = (result.OnePercentLowFps - _tuningBaseline.OnePercentLowFps) / _tuningBaseline.OnePercentLowFps * 100;
            TuningMeasureText = $"Ø {result.AvgFps} FPS ({deltaFps:+0.0;-0.0} %) · 1% Low {result.OnePercentLowFps} FPS ({deltaLow:+0.0;-0.0} %) vs. Baseline";
            RefreshBenchmarkState();
        }
        finally
        {
            TuningBusy = false;
        }
    }

    [RelayCommand]
    private void KeepCandidate()
    {
        if (SelectedExperimentProfile is null || _tuningLastMeasure is null || _tuningBaseline is null)
            return;

        var deltaFps = (_tuningLastMeasure.AvgFps - _tuningBaseline.AvgFps) / _tuningBaseline.AvgFps * 100;
        TuningLog.Insert(0, new TuningLogEntry(SelectedExperimentProfile.Name,
            $"{deltaFps:+0.0;-0.0} % Ø-FPS", "Behalten", true, deltaFps >= 0));

        ResetStep("Behalten ✓ — das System bleibt auf diesem Stand. Nächstes Experiment wählen oder Session abschließen.");
    }

    [RelayCommand]
    private async Task DiscardCandidateAsync()
    {
        if (TuningBusy || Analysis is null || _tuningBaselineProfileName is null)
            return;
        if (MsfsApplier.IsSimRunning())
        {
            TuningStatus = "Für den Rollback bitte MSFS beenden.";
            return;
        }

        TuningBusy = true;
        TuningStatus = "Rolle zurück auf die Baseline …";
        try
        {
            var baseline = ProfileService.List().FirstOrDefault(p => p.Name == _tuningBaselineProfileName);
            if (baseline is null)
            {
                TuningStatus = "Baseline-Profil nicht gefunden!";
                return;
            }
            var (ok, log) = await Task.Run(() => ProfileService.Apply(baseline, Analysis));
            _lastAppliedProfileName = baseline.Name;

            var deltaText = "—";
            var deltaGood = false;
            if (_tuningLastMeasure is not null && _tuningBaseline is not null)
            {
                var deltaFps = (_tuningLastMeasure.AvgFps - _tuningBaseline.AvgFps) / _tuningBaseline.AvgFps * 100;
                deltaText = $"{deltaFps:+0.0;-0.0} % Ø-FPS";
                deltaGood = deltaFps >= 0;
            }
            TuningLog.Insert(0, new TuningLogEntry(SelectedExperimentProfile?.Name ?? "Experiment", deltaText,
                ok ? "Verworfen (Baseline wiederhergestellt)" : "Rollback mit Fehlern!", false, deltaGood));

            ResetStep(ok
                ? "Zurück auf Baseline ✓ — nächstes Experiment wählen."
                : "Rollback teilweise fehlgeschlagen:\n" + string.Join("\n", log));
        }
        finally
        {
            TuningBusy = false;
        }
    }

    private void ResetStep(string status)
    {
        TuningApplied = false;
        TuningMeasured = false;
        TuningMeasureText = "";
        _tuningLastMeasure = null;
        TuningStatus = status;
        UpdateTuningStep();
    }

    [RelayCommand]
    private async Task FinishTuningAsync()
    {
        if (Analysis is null)
            return;
        var name = TuningFinalName.Trim();
        if (name.Length == 0)
            name = $"Tuning-Ergebnis {DateTime.Now:yyyy-MM-dd HH-mm}";

        await Task.Run(() => ProfileService.Capture(Analysis, name, isBackup: false));
        RefreshProfiles();

        var kept = TuningLog.Count(e => e.Kept);
        Controls.AppDialog.Info("Tuning abgeschlossen",
            $"Session abgeschlossen: {TuningLog.Count} Experiment(e), {kept} behalten.\n\nAktueller Stand als Profil „{name}“ gespeichert. Die Baseline bleibt als Rückfalloption auf der Profil-Seite.");
        TuningStatus = $"Session abgeschlossen — Ergebnis als „{name}“ gespeichert.";
    }
}
