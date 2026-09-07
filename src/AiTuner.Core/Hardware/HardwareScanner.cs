using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AiTuner.Core.Hardware;

public static class HardwareScanner
{
    public static HardwareSnapshot Scan()
    {
        var warnings = new List<string>();

        var snapshot = new HardwareSnapshot
        {
            Cpu = SafeGet(ReadCpu, warnings, "CPU"),
            Memory = SafeGet(ReadMemory, warnings, "Arbeitsspeicher"),
            Gpus = SafeGet(ReadGpus, warnings, "GPU") ?? [],
            Displays = SafeGet(ReadDisplays, warnings, "Anzeige") ?? [],
            Volumes = SafeGet(ReadVolumes, warnings, "Laufwerke") ?? [],
            OsDescription = SafeGet(ReadOs, warnings, "Windows-Version") ?? Environment.OSVersion.VersionString,
        };

        snapshot.Warnings.AddRange(warnings);
        return snapshot;
    }

    private static T? SafeGet<T>(Func<T> read, List<string> warnings, string label) where T : class
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            warnings.Add($"{label}: {ex.Message}");
            return null;
        }
    }

    private static int ToInt(object? value)
    {
        try { return value is null ? 0 : Convert.ToInt32(value); }
        catch { return 0; }
    }

    private static CpuInfo ReadCpu()
    {
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L3CacheSize FROM Win32_Processor");
        foreach (ManagementObject mo in searcher.Get())
        {
            var manufacturer = (mo["Manufacturer"] as string ?? "").Trim() switch
            {
                "AuthenticAMD" => "AMD",
                "GenuineIntel" => "Intel",
                var other => other,
            };
            return new CpuInfo(
                (mo["Name"] as string ?? "CPU").Trim(),
                manufacturer,
                ToInt(mo["NumberOfCores"]),
                ToInt(mo["NumberOfLogicalProcessors"]),
                ToInt(mo["MaxClockSpeed"]),
                ToInt(mo["L3CacheSize"]));
        }
        throw new InvalidOperationException("Keine CPU-Informationen über WMI verfügbar.");
    }

    private static MemoryInfo ReadMemory()
    {
        ulong totalBytes = 0;
        var modules = 0;
        var speed = 0;

        using var searcher = new ManagementObjectSearcher(
            "SELECT Capacity, ConfiguredClockSpeed, Speed FROM Win32_PhysicalMemory");
        foreach (ManagementObject mo in searcher.Get())
        {
            modules++;
            try { totalBytes += Convert.ToUInt64(mo["Capacity"]); } catch { }
            var clock = ToInt(mo["ConfiguredClockSpeed"]);
            if (clock == 0)
                clock = ToInt(mo["Speed"]);
            speed = Math.Max(speed, clock);
        }

        return new MemoryInfo(Math.Round(totalBytes / 1073741824.0, 1), modules, speed);
    }

    private static IReadOnlyList<GpuInfo> ReadGpus()
    {
        var gpus = new List<GpuInfo>();

        // Primary source: nvidia-smi ships with every NVIDIA driver and reports exact VRAM.
        var smi = ProcessRunner.Run("nvidia-smi",
            "--query-gpu=name,memory.total,driver_version --format=csv,noheader,nounits");
        if (smi is not null)
        {
            foreach (var line in smi.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length >= 3 && int.TryParse(parts[1], out var vramMb))
                    gpus.Add(new GpuInfo(parts[0], vramMb, parts[2], "nvidia-smi"));
            }
        }

        if (gpus.Count > 0)
        {
            // BAR1 size reveals Resizable BAR: with ReBAR on, BAR1 spans (at least) the whole VRAM.
            var memoryQuery = ProcessRunner.Run("nvidia-smi", "-q -d MEMORY");
            if (memoryQuery is not null)
            {
                var bar1Totals = System.Text.RegularExpressions.Regex.Matches(
                    memoryQuery, @"BAR1 Memory Usage[\s\S]*?Total\s*:\s*(\d+)\s*MiB");
                for (var i = 0; i < gpus.Count && i < bar1Totals.Count; i++)
                {
                    if (int.TryParse(bar1Totals[i].Groups[1].Value, out var bar1Mb))
                        gpus[i] = gpus[i] with { Bar1Mb = bar1Mb };
                }
            }
        }

        if (gpus.Count == 0)
        {
            // Fallback: WMI (AdapterRAM is a 32-bit value and caps at 4 GB).
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, AdapterRAM, DriverVersion FROM Win32_VideoController");
            foreach (ManagementObject mo in searcher.Get())
            {
                var vramMb = 0;
                try { vramMb = (int)(Convert.ToUInt64(mo["AdapterRAM"]) / (1024 * 1024)); } catch { }
                gpus.Add(new GpuInfo(
                    mo["Name"] as string ?? "GPU",
                    vramMb,
                    mo["DriverVersion"] as string ?? "?",
                    "WMI (VRAM evtl. ungenau)"));
            }
        }

        return gpus;
    }

    private static IReadOnlyList<VolumeInfo> ReadVolumes()
    {
        const string scope = @"\\.\root\Microsoft\Windows\Storage";

        var diskByNumber = new Dictionary<uint, (string Media, string Bus)>();
        using (var searcher = new ManagementObjectSearcher(scope,
            "SELECT DeviceId, MediaType, BusType FROM MSFT_PhysicalDisk"))
        {
            foreach (ManagementObject mo in searcher.Get())
            {
                if (uint.TryParse(mo["DeviceId"] as string, out var number))
                    diskByNumber[number] = (MediaTypeName(ToInt(mo["MediaType"])), BusTypeName(ToInt(mo["BusType"])));
            }
        }

        var letterToDisk = new Dictionary<char, uint>();
        using (var searcher = new ManagementObjectSearcher(scope,
            "SELECT DiskNumber, DriveLetter FROM MSFT_Partition"))
        {
            foreach (ManagementObject mo in searcher.Get())
            {
                var letter = mo["DriveLetter"] switch
                {
                    char c => c,
                    ushort u => (char)u,
                    _ => '\0',
                };
                if (letter != '\0' && letter != ' ')
                    letterToDisk[char.ToUpperInvariant(letter)] = (uint)ToInt(mo["DiskNumber"]);
            }
        }

        var volumes = new List<VolumeInfo>();
        foreach (var drive in System.IO.DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                continue;

            var letter = char.ToUpperInvariant(drive.Name[0]);
            var media = "?";
            var bus = "?";
            if (letterToDisk.TryGetValue(letter, out var number) && diskByNumber.TryGetValue(number, out var disk))
                (media, bus) = disk;

            volumes.Add(new VolumeInfo(
                $"{letter}:",
                media,
                bus,
                Math.Round(drive.TotalSize / 1073741824.0),
                Math.Round(drive.AvailableFreeSpace / 1073741824.0)));
        }

        return volumes;
    }

    private static string MediaTypeName(int mediaType) => mediaType switch
    {
        3 => "HDD",
        4 => "SSD",
        5 => "SCM",
        _ => "Unbekannt",
    };

    private static string BusTypeName(int busType) => busType switch
    {
        7 => "USB",
        8 => "RAID",
        10 => "SAS",
        11 => "SATA",
        17 => "NVMe",
        _ => $"Bus {busType}",
    };

    private static string ReadOs()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var product = key?.GetValue("ProductName") as string ?? "Windows";
        var displayVersion = key?.GetValue("DisplayVersion") as string ?? "";
        var build = key?.GetValue("CurrentBuildNumber") as string ?? "";

        // The registry still reports "Windows 10" on Windows 11; the build number tells them apart.
        if (int.TryParse(build, out var buildNumber) && buildNumber >= 22000)
            product = product.Replace("Windows 10", "Windows 11");

        return $"{product} {displayVersion} (Build {build})".Replace("  ", " ").Trim();
    }

    private static IReadOnlyList<DisplayInfo> ReadDisplays()
    {
        var displays = new List<DisplayInfo>();
        var device = new DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>() };

        for (uint i = 0; EnumDisplayDevices(null, i, ref device, 0); i++)
        {
            const uint attachedToDesktop = 0x1;
            const uint primaryDevice = 0x4;

            if ((device.StateFlags & attachedToDesktop) != 0)
            {
                var mode = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
                if (EnumDisplaySettings(device.DeviceName, -1, ref mode))
                {
                    displays.Add(new DisplayInfo(
                        device.DeviceString,
                        (int)mode.dmPelsWidth,
                        (int)mode.dmPelsHeight,
                        (int)mode.dmDisplayFrequency,
                        (device.StateFlags & primaryDevice) != 0));
                }
            }

            device = new DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>() };
        }

        return displays;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public uint cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }
}
