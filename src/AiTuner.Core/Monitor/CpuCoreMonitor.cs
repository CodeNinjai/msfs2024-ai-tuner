using System.Diagnostics;
using System.Runtime.InteropServices;
using AiTuner.Core.Apply;

namespace AiTuner.Core.Monitor;

public sealed record CoreMonitorSample(
    double[] CorePercents,
    bool SimRunning,
    double SimCpuPercent,
    double MaxThreadPercent,
    string AffinityText);

/// <summary>
/// Leichtgewichtiger 1-Hz-Sampler: Pro-Kern-Auslastung über NtQuerySystemInformation
/// (ein Syscall pro Messung) plus CPU-/Thread-Zeiten-Deltas des laufenden MSFS-Prozesses.
/// MaxThreadPercent ist die Last des stärksten Threads in Prozent EINES Kerns —
/// der „Mainthread limited“-Indikator.
/// </summary>
public sealed class CpuCoreMonitor
{
    private long[]? _prevIdle;
    private long[]? _prevTotal;
    private DateTime _prevWall = DateTime.UtcNow;
    private TimeSpan _prevProcessCpu;
    private int _prevPid = -1;
    private readonly Dictionary<int, TimeSpan> _threadTimes = new();

    public CoreMonitorSample Sample()
    {
        var count = Environment.ProcessorCount;
        var corePercents = new double[count];
        var now = DateTime.UtcNow;
        var elapsedMs = (now - _prevWall).TotalMilliseconds;

        SampleCores(count, corePercents);

        double simCpu = 0, maxThread = 0;
        var affinity = "—";
        var running = false;

        var process = MsfsApplier.RunningSimProcess();
        if (process is not null)
        {
            running = true;
            try
            {
                var samePid = _prevPid == process.Id && elapsedMs > 0;

                var processCpu = process.TotalProcessorTime;
                if (samePid)
                    simCpu = Math.Clamp(100.0 * (processCpu - _prevProcessCpu).TotalMilliseconds / (elapsedMs * count), 0, 100);
                _prevProcessCpu = processCpu;

                var currentTimes = new Dictionary<int, TimeSpan>();
                foreach (ProcessThread thread in process.Threads)
                {
                    TimeSpan threadCpu;
                    try { threadCpu = thread.TotalProcessorTime; } catch { continue; }
                    currentTimes[thread.Id] = threadCpu;
                    if (samePid && _threadTimes.TryGetValue(thread.Id, out var previous))
                        maxThread = Math.Max(maxThread, 100.0 * (threadCpu - previous).TotalMilliseconds / elapsedMs);
                }
                _threadTimes.Clear();
                foreach (var pair in currentTimes)
                    _threadTimes[pair.Key] = pair.Value;
                maxThread = Math.Clamp(maxThread, 0, 100);

                affinity = DescribeAffinity((long)process.ProcessorAffinity, count);
                _prevPid = process.Id;
            }
            catch
            {
                // Prozess kann zwischen Snapshot und Abfrage enden.
            }
        }
        else
        {
            _prevPid = -1;
            _threadTimes.Clear();
        }

        _prevWall = now;
        return new CoreMonitorSample(corePercents, running, Math.Round(simCpu, 1), Math.Round(maxThread), affinity);
    }

    private void SampleCores(int count, double[] corePercents)
    {
        var size = Marshal.SizeOf<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(size * count);
        try
        {
            if (NtQuerySystemInformation(SystemProcessorPerformanceInformation, buffer, size * count, out _) != 0)
                return;

            var idle = new long[count];
            var total = new long[count];
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>(buffer + i * size);
                idle[i] = info.IdleTime;
                total[i] = info.KernelTime + info.UserTime; // KernelTime enthält IdleTime
            }

            if (_prevIdle is not null && _prevTotal is not null && _prevIdle.Length == count)
            {
                for (var i = 0; i < count; i++)
                {
                    var totalDelta = total[i] - _prevTotal[i];
                    var idleDelta = idle[i] - _prevIdle[i];
                    corePercents[i] = totalDelta > 0
                        ? Math.Clamp(100.0 * (totalDelta - idleDelta) / totalDelta, 0, 100)
                        : 0;
                }
            }

            _prevIdle = idle;
            _prevTotal = total;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string DescribeAffinity(long mask, int count)
    {
        var all = count >= 64 ? -1L : (1L << count) - 1;
        var firstHalf = (1L << (count / 2)) - 1;
        if (mask == all)
            return "Alle Kerne";
        if (mask == firstHalf)
            return $"CCD0 (Kerne 0–{count / 2 - 1})";
        return $"Maske 0x{mask:X}";
    }

    private const int SystemProcessorPerformanceInformation = 8;

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int informationClass, IntPtr information, int length, out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION
    {
        public long IdleTime;
        public long KernelTime;
        public long UserTime;
        public long DpcTime;
        public long InterruptTime;
        public uint InterruptCount;
    }
}
