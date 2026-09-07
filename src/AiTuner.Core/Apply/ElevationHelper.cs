using System.Diagnostics;

namespace AiTuner.Core.Apply;

/// <summary>Relaunches the current executable elevated (UAC prompt) for a single command.</summary>
public static class ElevationHelper
{
    public static (bool Ok, string Message) RunSelfElevated(params string[] args)
    {
        try
        {
            var exePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Eigener Prozesspfad unbekannt.");

            var startInfo = new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                Verb = "runas",
            };
            foreach (var arg in args)
                startInfo.ArgumentList.Add(arg);

            using var process = Process.Start(startInfo);
            if (process is null)
                return (false, "Prozessstart fehlgeschlagen.");

            if (!process.WaitForExit(60000))
                return (false, "Zeitüberschreitung beim Warten auf den Admin-Prozess.");

            return process.ExitCode == 0
                ? (true, "Mit Administratorrechten angewendet.")
                : (false, $"Admin-Vorgang fehlgeschlagen (Exit-Code {process.ExitCode}).");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (false, "Abgebrochen — Administratorrechte wurden nicht erteilt.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Wie RunSelfElevated, aber ohne auf das Ende zu warten — für langlaufende Helfer
    /// (z.B. Flug-Modus, der bis zum Sim-Ende aktiv bleibt).</summary>
    public static (bool Ok, string Message) RunSelfElevatedDetached(params string[] args)
    {
        try
        {
            var exePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Eigener Prozesspfad unbekannt.");

            var startInfo = new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                Verb = "runas",
            };
            foreach (var arg in args)
                startInfo.ArgumentList.Add(arg);

            using var process = Process.Start(startInfo);
            return process is null
                ? (false, "Prozessstart fehlgeschlagen.")
                : (true, "Helfer mit Administratorrechten gestartet.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (false, "Abgebrochen — Administratorrechte wurden nicht erteilt.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
