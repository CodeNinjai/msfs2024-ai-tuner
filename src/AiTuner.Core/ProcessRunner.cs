using System.Diagnostics;

namespace AiTuner.Core;

/// <summary>Runs external diagnostic tools (nvidia-smi, powercfg) without a console window.</summary>
public static class ProcessRunner
{
    public static string? Run(string fileName, string arguments, int timeoutMs = 8000)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
                return null;

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(); } catch { }
                return null;
            }
            return process.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }
}
