using Microsoft.Win32;

namespace AiTuner.Core.Apply;

/// <summary>
/// Both executables (App and CLI) call TryHandle at startup. When the process was relaunched
/// elevated with "--apply-reg …", the registry write happens here and the process exits —
/// this is how HKLM settings (HAGS, MPO) are changed without running the whole app as admin.
/// </summary>
public static class ElevatedCommands
{
    public static bool TryHandle(string[] args, out int exitCode)
    {
        exitCode = 0;

        // --svc-flightmode <name1,name2,…>: Dienste stoppen, Sim-Ende abwarten, wiederherstellen.
        if (args.Length >= 2 && args[0] == "--svc-flightmode")
        {
            exitCode = ServicePauseService.RunFlightMode(args[1]);
            return true;
        }

        // --svc-resume: zuvor gestoppte Dienste sofort wiederherstellen.
        if (args.Length >= 1 && args[0] == "--svc-resume")
        {
            exitCode = ServicePauseService.Resume();
            return true;
        }

        if (args.Length < 5 || args[0] != "--apply-reg")
            return false;

        try
        {
            // --apply-reg <HKLM|HKCU> <path> <name> <dword|delete> [value]
            var root = args[1].Equals("HKLM", StringComparison.OrdinalIgnoreCase)
                ? Registry.LocalMachine
                : Registry.CurrentUser;
            var path = args[2];
            var name = args[3];
            var kind = args[4];

            if (kind.Equals("delete", StringComparison.OrdinalIgnoreCase))
            {
                using var key = root.OpenSubKey(path, writable: true);
                key?.DeleteValue(name, throwOnMissingValue: false);
            }
            else
            {
                using var key = root.CreateSubKey(path);
                key.SetValue(name, int.Parse(args[5]), RegistryValueKind.DWord);
            }
        }
        catch
        {
            exitCode = 1;
        }
        return true;
    }
}
