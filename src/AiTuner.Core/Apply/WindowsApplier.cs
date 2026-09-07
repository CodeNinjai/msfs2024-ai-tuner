using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AiTuner.Core.Apply;

/// <summary>Applies Windows-side settings. HKCU writes are direct; HKLM goes through UAC elevation.</summary>
public static class WindowsApplier
{
    public static (bool Ok, string Message) SetCurrentUserDword(string path, string name, int value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(path);
            key.SetValue(name, value, RegistryValueKind.DWord);
            return (true, $"{name} = {value} gesetzt.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static (bool Ok, string Message) DeleteCurrentUserValue(string path, string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(path, writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
            return (true, $"{name} entfernt.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static (bool Ok, string Message) SetLocalMachineDword(string path, string name, int value)
        => ElevationHelper.RunSelfElevated("--apply-reg", "HKLM", path, name, "dword", value.ToString());

    public static (bool Ok, string Message) DeleteLocalMachineValue(string path, string name)
        => ElevationHelper.RunSelfElevated("--apply-reg", "HKLM", path, name, "delete");

    public static (bool Ok, string Message) SetActivePowerPlan(Guid schemeGuid)
    {
        var result = PowerSetActiveScheme(IntPtr.Zero, ref schemeGuid);
        return result == 0
            ? (true, "Energieplan umgestellt.")
            : (false, $"PowerSetActiveScheme-Fehler {result} — existiert dieser Plan auf dem System?");
    }

    public static int? ReadDword(RegistryKey root, string path, string name)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            return key?.GetValue(name) as int?;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);
}
