namespace AiTuner.Core.Apply;

/// <summary>
/// Pins the running MSFS process to the first CCD (logical processors 0..n/2-1).
/// On dual-CCD X3D CPUs (7900X3D/7950X3D/9900X3D/9950X3D) CCD0 carries the 3D V-Cache.
/// </summary>
public static class AffinityService
{
    public static (bool Ok, string Message) PinSimToFirstCcd()
    {
        var process = MsfsApplier.RunningSimProcess();
        if (process is null)
            return (false, "MSFS läuft nicht — das Pinning wirkt auf den laufenden Sim-Prozess.");

        var logicalProcessors = Environment.ProcessorCount;
        if (logicalProcessors < 4)
            return (false, "Zu wenige logische Prozessoren für ein sinnvolles Pinning.");

        var half = logicalProcessors / 2;
        var mask = (1L << half) - 1;

        try
        {
            process.ProcessorAffinity = (IntPtr)mask;
            return (true, $"MSFS ({process.ProcessName}) auf die ersten {half} logischen Prozessoren (CCD0, V-Cache) gepinnt. Gilt bis zum Sim-Neustart.");
        }
        catch (Exception ex)
        {
            return (false, "Affinität konnte nicht gesetzt werden: " + ex.Message);
        }
    }

    /// <summary>Pinnt MSFS auf die Performance-Kerne einer Hybrid-CPU (Intel P/E, via EfficiencyClass).</summary>
    public static (bool Ok, string Message) PinSimToPerformanceCores()
    {
        var process = MsfsApplier.RunningSimProcess();
        if (process is null)
            return (false, "MSFS läuft nicht — das Pinning wirkt auf den laufenden Sim-Prozess.");

        var topology = Hardware.CpuTopology.Detect();
        if (topology is not { IsHybrid: true } || topology.PCoreMask == 0)
            return (false, "Keine Hybrid-CPU (P/E-Kerne) erkannt.");

        try
        {
            process.ProcessorAffinity = (IntPtr)topology.PCoreMask;
            return (true, $"MSFS ({process.ProcessName}) auf die {topology.PCoreCount} P-Kerne ({topology.PLogicalCount} logische Prozessoren) gepinnt. Gilt bis zum Sim-Neustart.");
        }
        catch (Exception ex)
        {
            return (false, "Affinität konnte nicht gesetzt werden: " + ex.Message);
        }
    }

    /// <summary>Wählt automatisch das passende Pinning: V-Cache-CCD (Dual-CCD X3D) oder P-Kerne (Hybrid).</summary>
    public static (bool Ok, string Message) PinSimToFastCores(bool isDualCcdX3d)
    {
        if (Hardware.CpuTopology.Detect() is { IsHybrid: true })
            return PinSimToPerformanceCores();
        if (isDualCcdX3d)
            return PinSimToFirstCcd();
        return PinSimToFirstCcd(); // generischer Fallback: erste Kernhälfte
    }

    public static (bool Ok, string Message) ResetSimAffinity()
    {
        var process = MsfsApplier.RunningSimProcess();
        if (process is null)
            return (false, "MSFS läuft nicht.");

        try
        {
            var mask = (1L << Environment.ProcessorCount) - 1;
            process.ProcessorAffinity = (IntPtr)mask;
            return (true, "Affinität zurückgesetzt — MSFS darf wieder alle Kerne nutzen.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
