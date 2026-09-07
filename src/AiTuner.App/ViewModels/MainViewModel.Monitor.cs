using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using AiTuner.Core.Monitor;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiTuner.App.ViewModels;

/// <summary>Ein Kern-Balken im Live-Monitor (wird pro Tick aktualisiert, nicht neu erzeugt).</summary>
public sealed partial class CoreBarVm : ObservableObject
{
    public int Index { get; init; }
    public bool IsFirstCcd { get; init; }
    public Thickness BarMargin { get; init; }

    [ObservableProperty] private double _barHeight = 1;
    [ObservableProperty] private string _tooltip = "";
}

/// <summary>CPU-Kern-Monitor für den laufenden MSFS-Prozess (manuell startbar, 1 Hz).</summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<CoreBarVm> CoreBars { get; } = new();

    [ObservableProperty] private bool _isMonitorRunning;
    [ObservableProperty] private string _monitorButtonText = "Monitor starten";
    [ObservableProperty] private string _monitorStatus = "Gestoppt. Misst 1×/Sekunde mit minimaler Eigenlast (ein Syscall + Thread-Zeiten).";
    [ObservableProperty] private string _ccd0Label = "";
    [ObservableProperty] private string _ccd1Label = "";

    private DispatcherTimer? _monitorTimer;
    private CpuCoreMonitor? _coreMonitor;

    [RelayCommand]
    private void ToggleMonitor()
    {
        if (IsMonitorRunning)
        {
            StopMonitor();
            return;
        }

        var count = Environment.ProcessorCount;
        if (CoreBars.Count != count)
        {
            CoreBars.Clear();
            var cpu = Analysis?.Hardware.Cpu;
            var dualCcdX3d = cpu is not null && cpu.Manufacturer == "AMD" && cpu.L3CacheKb >= 96 * 1024 && cpu.Cores > 8;
            var topology = Core.Hardware.CpuTopology.Detect();

            // Gruppengrenze: bei Hybrid-CPUs zwischen P- und E-Kernen, sonst zwischen den Kernhälften (CCDs).
            var splitIndex = count / 2;
            var isFast = (int i) => i < count / 2;
            if (topology is { IsHybrid: true })
            {
                var classes = topology.EfficiencyClassByLogical;
                var maxClass = classes.Max();
                isFast = i => i < classes.Length && classes[i] == maxClass;
                splitIndex = topology.PLogicalCount;
                Ccd0Label = $"P-Kerne ({topology.PCoreCount}× · logisch 0–{splitIndex - 1})";
                Ccd1Label = $"E-Kerne ({topology.ECoreCount}× · logisch {splitIndex}–{count - 1})";
            }
            else
            {
                Ccd0Label = dualCcdX3d ? $"CCD0 · V-Cache (Kerne 0–{count / 2 - 1})" : $"Kerne 0–{count / 2 - 1}";
                Ccd1Label = dualCcdX3d ? $"CCD1 · Frequenz (Kerne {count / 2}–{count - 1})" : $"Kerne {count / 2}–{count - 1}";
            }

            for (var i = 0; i < count; i++)
                CoreBars.Add(new CoreBarVm
                {
                    Index = i,
                    IsFirstCcd = isFast(i),
                    BarMargin = new Thickness(i == splitIndex ? 18 : 2, 0, 2, 0),
                });
        }

        _coreMonitor = new CpuCoreMonitor();
        try { _coreMonitor.Sample(); } catch { } // Basislinie für die Deltas

        _monitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _monitorTimer.Tick += (_, _) => MonitorTick();
        _monitorTimer.Start();

        IsMonitorRunning = true;
        MonitorButtonText = "Monitor stoppen";
        MonitorStatus = "Messe …";
    }

    private void StopMonitor()
    {
        _monitorTimer?.Stop();
        _monitorTimer = null;
        _coreMonitor = null;
        IsMonitorRunning = false;
        MonitorButtonText = "Monitor starten";
        MonitorStatus = "Gestoppt. Misst 1×/Sekunde mit minimaler Eigenlast (ein Syscall + Thread-Zeiten).";
    }

    private void MonitorTick()
    {
        if (_coreMonitor is null)
            return;

        CoreMonitorSample sample;
        try { sample = _coreMonitor.Sample(); } catch { return; }

        for (var i = 0; i < CoreBars.Count && i < sample.CorePercents.Length; i++)
        {
            CoreBars[i].BarHeight = Math.Max(1.5, sample.CorePercents[i] * 0.64);
            CoreBars[i].Tooltip = $"Kern {i}: {sample.CorePercents[i]:0} %";
        }

        MonitorStatus = sample.SimRunning
            ? $"MSFS läuft · {sample.SimCpuPercent:0.0} % CPU gesamt · stärkster Thread: {sample.MaxThreadPercent:0} % eines Kerns · Affinität: {sample.AffinityText}"
            : "MSFS läuft nicht — Balken zeigen die Systemlast pro Kern.";
    }
}
