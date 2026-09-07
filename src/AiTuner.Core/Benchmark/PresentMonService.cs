using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AiTuner.Core.Apply;

namespace AiTuner.Core.Benchmark;

public sealed record BenchmarkResult(
    string Label,
    DateTimeOffset At,
    int DurationSeconds,
    int FrameCount,
    double AvgFps,
    double OnePercentLowFps,
    double AvgFrameTimeMs,
    double MaxFrameTimeMs)
{
    /// <summary>Referenzmessung für die Delta-Anzeige (genau eine in der Liste).</summary>
    public bool IsBaseline { get; set; }

    /// <summary>Zuletzt angewendetes Profil zum Zeitpunkt der Messung (null = unbekannt/geändert).</summary>
    public string? ProfileNote { get; set; }

    // Sim-Kontext via SimConnect (null/0 = Sim-Anbindung war nicht verfügbar) — für die
    // Vergleichbarkeits-Prüfung „gleicher Ort, gleiches Flugzeug, gleiche Phase?“.
    public string? Aircraft { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public bool? OnGround { get; set; }
}

/// <summary>
/// Frametime capture via Intel PresentMon (open source). The exe is downloaded on demand into
/// %APPDATA%\Msfs2024AiTuner\tools. ETW capture requires admin, so PresentMon itself is
/// started elevated (one UAC prompt per run).
/// </summary>
public static class PresentMonService
{
    private static string AppDataDirectory
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Msfs2024AiTuner");

    public static string ToolPath => Path.Combine(AppDataDirectory, "tools", "PresentMon.exe");

    public static string ResultsPath => Path.Combine(AppDataDirectory, "benchmarks.json");

    public static bool IsAvailable => File.Exists(ToolPath);

    private static readonly string[] DownloadUrls =
    [
        "https://github.com/GameTechDev/PresentMon/releases/download/v2.3.1/PresentMon-2.3.1-x64.exe",
        "https://github.com/GameTechDev/PresentMon/releases/download/v2.3.0/PresentMon-2.3.0-x64.exe",
        "https://github.com/GameTechDev/PresentMon/releases/download/v2.2.0/PresentMon-2.2.0-x64.exe",
        "https://github.com/GameTechDev/PresentMon/releases/download/v1.10.0/PresentMon-1.10.0-x64.exe",
    ];

    public static async Task<(bool Ok, string Message)> DownloadAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ToolPath)!);
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(60);

        foreach (var url in DownloadUrls)
        {
            try
            {
                var bytes = await client.GetByteArrayAsync(url);
                if (bytes.Length < 100_000)
                    continue;
                await File.WriteAllBytesAsync(ToolPath, bytes);
                return (true, $"PresentMon heruntergeladen ({bytes.Length / 1024} KB) → {ToolPath}");
            }
            catch
            {
                // nächste Version probieren
            }
        }
        return (false, $"Download fehlgeschlagen. PresentMon.exe kann auch manuell nach {ToolPath} gelegt werden (github.com/GameTechDev/PresentMon).");
    }

    /// <summary>Runs a timed capture. Blocks for the duration; call from a background thread.</summary>
    public static (bool Ok, string Message, BenchmarkResult? Result) RunCapture(string processName, int seconds, string label)
    {
        if (!IsAvailable)
            return (false, "PresentMon.exe fehlt — zuerst herunterladen.", null);

        var csvPath = Path.Combine(AppDataDirectory, "tools", $"capture-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        var arguments =
            $"--process_name {processName} --output_file \"{csvPath}\" --timed {seconds} --terminate_after_timed --stop_existing_session";

        try
        {
            // ETW braucht Adminrechte → PresentMon selbst läuft elevated.
            var startInfo = new ProcessStartInfo(ToolPath, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Minimized,
            };
            using var process = Process.Start(startInfo);
            if (process is null)
                return (false, "PresentMon konnte nicht gestartet werden.", null);

            if (!process.WaitForExit((seconds + 45) * 1000))
            {
                try { process.Kill(); } catch { }
                return (false, "PresentMon hat nicht rechtzeitig beendet.", null);
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (false, "Abgebrochen — PresentMon benötigt Administratorrechte (ETW-Capture).", null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }

        if (!File.Exists(csvPath))
            return (false, "Keine Messdaten erzeugt — präsentiert der Zielprozess gerade Frames?", null);

        var result = ParseCsv(csvPath, seconds, label);
        try { File.Delete(csvPath); } catch { }

        return result is null
            ? (false, "Messdatei enthielt keine auswertbaren Frames.", null)
            : (true, $"{result.FrameCount} Frames erfasst.", result);
    }

    private static BenchmarkResult? ParseCsv(string csvPath, int seconds, string label)
    {
        string[] lines;
        try { lines = File.ReadAllLines(csvPath); } catch { return null; }
        if (lines.Length < 2)
            return null;

        var header = lines[0].Split(',');
        var columnIndex = -1;
        foreach (var candidate in new[] { "MsBetweenPresents", "msBetweenPresents", "FrameTime", "CPUFrameTime", "MsBetweenDisplayChange" })
        {
            columnIndex = Array.FindIndex(header, h => h.Trim().Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (columnIndex >= 0)
                break;
        }
        if (columnIndex < 0)
            return null;

        var frameTimes = new List<double>();
        foreach (var line in lines.Skip(1))
        {
            var parts = line.Split(',');
            if (parts.Length <= columnIndex)
                continue;
            if (double.TryParse(parts[columnIndex], NumberStyles.Any, CultureInfo.InvariantCulture, out var ms) && ms > 0)
                frameTimes.Add(ms);
        }
        // Die erste Frame-Zeit ist oft ein Ausreißer (Capture-Start).
        if (frameTimes.Count > 10)
            frameTimes.RemoveAt(0);
        if (frameTimes.Count < 10)
            return null;

        var avgMs = frameTimes.Average();
        var maxMs = frameTimes.Max();
        var sorted = frameTimes.OrderByDescending(t => t).ToList();
        var worstCount = Math.Max(1, frameTimes.Count / 100);
        var worstAvgMs = sorted.Take(worstCount).Average();

        return new BenchmarkResult(
            label,
            DateTimeOffset.Now,
            seconds,
            frameTimes.Count,
            Math.Round(1000.0 / avgMs, 1),
            Math.Round(1000.0 / worstAvgMs, 1),
            Math.Round(avgMs, 2),
            Math.Round(maxMs, 2));
    }

    public static List<BenchmarkResult> LoadResults()
    {
        try
        {
            if (File.Exists(ResultsPath))
                return JsonSerializer.Deserialize<List<BenchmarkResult>>(File.ReadAllText(ResultsPath)) ?? new();
        }
        catch { }
        return new List<BenchmarkResult>();
    }

    public static void SaveResult(BenchmarkResult result)
    {
        var results = LoadResults();
        results.Insert(0, result);
        SaveAll(results);
    }

    public static void SaveAll(List<BenchmarkResult> results)
    {
        Directory.CreateDirectory(AppDataDirectory);
        File.WriteAllText(ResultsPath, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static string? RunningSimProcessName() => MsfsApplier.RunningSimProcessName();
}
