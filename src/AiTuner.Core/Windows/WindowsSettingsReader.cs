using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AiTuner.Core.Windows;

public sealed record WindowsSettingItem(string Category, string Name, string Value, string? Detail = null, string? Key = null);

public sealed class WindowsSettingsSnapshot
{
    public IReadOnlyList<WindowsSettingItem> Items { get; init; } = [];
    public List<string> Warnings { get; } = new();
}

public static class WindowsSettingsReader
{
    public static WindowsSettingsSnapshot Read()
    {
        var items = new List<WindowsSettingItem>();
        var warnings = new List<string>();

        ReadPowerPlan(items, warnings);

        items.Add(RegItem("Grafik", "Hardwarebeschleunigte GPU-Planung (HAGS)",
            Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode",
            v => v == 2 ? "Ein" : "Aus", "Nicht gesetzt", "hags"));

        items.Add(RegItem("Grafik", "Multiplane Overlay (MPO)",
            Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode",
            v => v == 5 ? "Deaktiviert" : $"Wert {v}", "Aktiv (Standard)", "mpo"));

        ReadDirectXGlobalSettings(items, warnings);

        items.Add(RegItem("Gaming", "Spielemodus",
            Registry.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled",
            v => v == 1 ? "Ein" : "Aus", "Ein (Standard)", "gamemode"));

        items.Add(RegItem("Gaming", "Hintergrundaufzeichnung („Aufzeichnen, was geschehen ist“)",
            Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled",
            v => v == 1 ? "Ein" : "Aus", "Aus (Standard)", "historicalcapture"));

        items.Add(RegItem("Gaming", "Game DVR (Capture-Infrastruktur)",
            Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled",
            v => v == 1 ? "Ein" : "Aus", "Ein (Standard)", "gamedvr"));

        items.Add(RegItem("Gaming", "App-Aufzeichnung (AppCapture)",
            Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled",
            v => v == 1 ? "Ein" : "Aus", "Ein (Standard)", "gamebarcapture"));

        items.Add(RegItem("System", "Visuelle Effekte",
            Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting",
            v => v switch
            {
                0 => "Automatisch",
                1 => "Beste Darstellung",
                2 => "Beste Leistung",
                3 => "Benutzerdefiniert",
                _ => $"Wert {v}",
            },
            "Automatisch (Standard)", "visualfx"));

        items.Add(RegItem("Sicherheit", "Speicherintegrität (HVCI)",
            Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled",
            v => v == 1 ? "Ein" : "Aus", "Aus", "hvci"));

        ReadPageFile(items, warnings);

        var snapshot = new WindowsSettingsSnapshot { Items = items };
        snapshot.Warnings.AddRange(warnings);
        return snapshot;
    }

    private static void ReadPowerPlan(List<WindowsSettingItem> items, List<string> warnings)
    {
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out var guidPtr) != 0 || guidPtr == IntPtr.Zero)
            {
                items.Add(new WindowsSettingItem("Energie", "Aktiver Energieplan", "Unbekannt", Key: "powerplan"));
                return;
            }
            try
            {
                var guid = Marshal.PtrToStructure<Guid>(guidPtr);
                items.Add(new WindowsSettingItem("Energie", "Aktiver Energieplan",
                    ReadSchemeFriendlyName(guid) ?? "Unbekannt", guid.ToString(), "powerplan"));
            }
            finally
            {
                LocalFree(guidPtr);
            }
        }
        catch (Exception ex)
        {
            warnings.Add("Energieplan: " + ex.Message);
        }
    }

    private static string? ReadSchemeFriendlyName(Guid scheme)
    {
        uint size = 0;
        if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size) != 0 || size == 0)
            return null;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != 0)
                return null;
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
    private static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid,
        IntPtr subGroupGuid, IntPtr powerSettingGuid, IntPtr buffer, ref uint bufferSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private static void ReadDirectXGlobalSettings(List<WindowsSettingItem> items, List<string> warnings)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
            var value = key?.GetValue("DirectXUserGlobalSettings") as string;
            items.Add(new WindowsSettingItem("Grafik", "DirectX-Globaleinstellungen (VRR/Auto-HDR)",
                string.IsNullOrWhiteSpace(value) ? "Nicht gesetzt (Standard)" : value));
        }
        catch (Exception ex)
        {
            warnings.Add("DirectX-Einstellungen: " + ex.Message);
        }
    }

    private static void ReadPageFile(List<WindowsSettingItem> items, List<string> warnings)
    {
        try
        {
            var entries = new List<string>();
            using var searcher = new ManagementObjectSearcher("SELECT Name, AllocatedBaseSize FROM Win32_PageFileUsage");
            foreach (ManagementObject mo in searcher.Get())
            {
                var name = mo["Name"] as string ?? "?";
                int size;
                try { size = Convert.ToInt32(mo["AllocatedBaseSize"]); } catch { size = 0; }
                entries.Add($"{name}: {size} MB");
            }
            items.Add(new WindowsSettingItem("System", "Auslagerungsdatei",
                entries.Count > 0 ? string.Join(" · ", entries) : "Keine"));
        }
        catch (Exception ex)
        {
            warnings.Add("Auslagerungsdatei: " + ex.Message);
        }
    }

    private static WindowsSettingItem RegItem(
        string category, string name,
        RegistryKey root, string path, string valueName,
        Func<int, string> format, string missingText, string? key = null)
    {
        try
        {
            using var regKey = root.OpenSubKey(path);
            var raw = regKey?.GetValue(valueName);
            if (raw is int value)
                return new WindowsSettingItem(category, name, format(value), $@"{ShortHive(root)}\{path}\{valueName} = {value}", key);
            return new WindowsSettingItem(category, name, missingText, $@"{ShortHive(root)}\{path}\{valueName} (nicht gesetzt)", key);
        }
        catch (Exception ex)
        {
            return new WindowsSettingItem(category, name, "Nicht lesbar", ex.Message, key);
        }
    }

    private static string ShortHive(RegistryKey root)
        => root.Name.StartsWith("HKEY_LOCAL_MACHINE") ? "HKLM" : "HKCU";
}
