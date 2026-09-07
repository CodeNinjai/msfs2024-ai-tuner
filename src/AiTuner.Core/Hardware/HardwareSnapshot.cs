namespace AiTuner.Core.Hardware;

/// <summary>L3CacheKb helps the rule engine detect X3D CPUs (large V-Cache) and CCD topology hints.</summary>
public sealed record CpuInfo(string Name, string Manufacturer, int Cores, int Threads, int MaxClockMhz, int L3CacheKb);

public sealed record MemoryInfo(double TotalGb, int ModuleCount, int SpeedMtps);

/// <summary>Bar1Mb ≈ VRAM size means Resizable BAR is active; ~256 MB means it is off.</summary>
public sealed record GpuInfo(string Name, int VramMb, string DriverVersion, string Source, int? Bar1Mb = null)
{
    public string ResizableBarState =>
        Bar1Mb is null ? "Unbekannt"
        : Bar1Mb >= Math.Max(4096, VramMb / 2) ? "Aktiv"
        : "Inaktiv";
}

public sealed record DisplayInfo(string DeviceName, int Width, int Height, int RefreshHz, bool IsPrimary);

public sealed record VolumeInfo(string DriveLetter, string MediaType, string BusType, double TotalGb, double FreeGb);

public sealed class HardwareSnapshot
{
    public CpuInfo? Cpu { get; init; }
    public MemoryInfo? Memory { get; init; }
    public IReadOnlyList<GpuInfo> Gpus { get; init; } = [];
    public IReadOnlyList<DisplayInfo> Displays { get; init; } = [];
    public IReadOnlyList<VolumeInfo> Volumes { get; init; } = [];
    public string OsDescription { get; init; } = "";
    public List<string> Warnings { get; } = new();
}
