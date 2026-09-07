using System.Runtime.InteropServices;

namespace AiTuner.Core.Hardware;

/// <summary>Ergebnis der Kern-Topologie-Abfrage (Intel-Hybrid: P-/E-Kerne über EfficiencyClass).</summary>
public sealed record CpuTopologyInfo(
    bool IsHybrid,
    int PCoreCount,               // physische Performance-Kerne
    int ECoreCount,               // physische Efficiency-Kerne
    int PLogicalCount,            // logische Prozessoren der P-Kerne (inkl. Hyper-Threading)
    long PCoreMask,               // Affinitätsmaske der P-Kern-Logicals
    byte[] EfficiencyClassByLogical); // logischer Index → EfficiencyClass (höchste = P)

/// <summary>
/// Fragt die CPU-Set-Informationen von Windows ab (GetSystemCpuSetInformation). Auf Hybrid-CPUs
/// (Intel 12th Gen+, „Alder Lake“ aufwärts) tragen P-Kerne eine höhere EfficiencyClass als E-Kerne —
/// das ist die offizielle, herstellerneutrale Erkennung (funktioniert theoretisch auch für künftige
/// AMD-Hybrid-Designs). Homogene CPUs liefern überall dieselbe Klasse → IsHybrid = false.
/// </summary>
public static class CpuTopology
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemCpuSetInformation(
        IntPtr information, uint bufferLength, out uint returnedLength, IntPtr process, uint flags);

    private static CpuTopologyInfo? _cached;
    private static bool _detected;

    public static CpuTopologyInfo? Detect()
    {
        if (_detected)
            return _cached;
        _detected = true;
        try
        {
            _cached = Query();
        }
        catch
        {
            _cached = null;
        }
        return _cached;
    }

    private static CpuTopologyInfo? Query()
    {
        GetSystemCpuSetInformation(IntPtr.Zero, 0, out var needed, IntPtr.Zero, 0);
        if (needed == 0)
            return null;

        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!GetSystemCpuSetInformation(buffer, needed, out var returned, IntPtr.Zero, 0))
                return null;

            // SYSTEM_CPU_SET_INFORMATION: Size(0,u32) Type(4,u32) Id(8,u32) Group(12,u16)
            // LogicalProcessorIndex(14,u8) CoreIndex(15,u8) LastLevelCacheIndex(16,u8)
            // NumaNodeIndex(17,u8) EfficiencyClass(18,u8) …
            var entries = new List<(byte Logical, byte Core, byte Efficiency)>();
            var offset = 0;
            while (offset + 20 <= (int)returned)
            {
                var size = Marshal.ReadInt32(buffer, offset);
                if (size <= 0)
                    break;
                var type = Marshal.ReadInt32(buffer, offset + 4);
                if (type == 0) // CpuSetInformation
                {
                    entries.Add((
                        Marshal.ReadByte(buffer, offset + 14),
                        Marshal.ReadByte(buffer, offset + 15),
                        Marshal.ReadByte(buffer, offset + 18)));
                }
                offset += size;
            }

            if (entries.Count == 0)
                return null;

            var maxClass = entries.Max(e => e.Efficiency);
            var minClass = entries.Min(e => e.Efficiency);
            var isHybrid = maxClass != minClass;

            var pEntries = entries.Where(e => e.Efficiency == maxClass).ToList();
            var eEntries = entries.Where(e => e.Efficiency != maxClass).ToList();

            long pMask = 0;
            foreach (var entry in pEntries)
                if (entry.Logical < 64)
                    pMask |= 1L << entry.Logical;

            var classByLogical = new byte[entries.Max(e => e.Logical) + 1];
            foreach (var entry in entries)
                classByLogical[entry.Logical] = entry.Efficiency;

            return new CpuTopologyInfo(
                isHybrid,
                PCoreCount: pEntries.Select(e => e.Core).Distinct().Count(),
                ECoreCount: eEntries.Select(e => e.Core).Distinct().Count(),
                PLogicalCount: pEntries.Count,
                PCoreMask: pMask,
                EfficiencyClassByLogical: classByLogical);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
