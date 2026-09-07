using System.Diagnostics;
using System.ServiceProcess;
using System.Text.Json;

namespace AiTuner.Core.Apply;

/// <summary>Ein pausierbarer Windows-Dienst aus der kuratierten Whitelist.</summary>
public sealed record PausableService(
    string Name,           // Dienstname (sc)
    string Display,        // Anzeigename
    string Reason,         // warum das Pausieren während des Flugs hilft
    string WhileStopped,   // was während der Pause nicht funktioniert
    bool DefaultChecked);

/// <summary>
/// „Flug-Modus“: kuratierte Windows-Dienste temporär stoppen, solange MSFS läuft, und danach
/// automatisch wiederherstellen. Bewusst KEINE dauerhaften Änderungen (Startart bleibt unberührt),
/// nur Whitelist, Zustand wird auf Platte gemerkt (Absturz-sicher).
/// </summary>
public static class ServicePauseService
{
    /// <summary>Kuratierte Whitelist — nur Dienste, deren Pause für Windows unkritisch ist.</summary>
    public static readonly IReadOnlyList<PausableService> Catalog = new List<PausableService>
    {
        new("wuauserv", "Windows Update",
            "Verhindert Update-Downloads/-Installationen mitten im Flug (Bandbreite, Disk-I/O, Ruckler).",
            "Updates werden erst nach dem Flug wieder gesucht/installiert.", true),
        new("DoSvc", "Übermittlungsoptimierung",
            "Update-P2P-Downloads (auch Upload an fremde PCs!) pausieren — der häufigste heimliche Bandbreitenfresser.",
            "Keine Update-Downloads; danach automatisch wieder aktiv.", true),
        new("BITS", "Hintergrundübertragung (BITS)",
            "Hintergrund-Downloads von Windows/Store/Apps pausieren.",
            "Laufende Hintergrund-Downloads pausieren und setzen später fort.", true),
        new("WSearch", "Windows-Suche (Indexer)",
            "Indizierungs-I/O auf der Platte vermeiden — relevant beim Streamen großer Szenerien.",
            "Startmenü-/Explorer-Suche liefert währenddessen eingeschränkte Ergebnisse.", true),
        new("SysMain", "SysMain (Superfetch)",
            "Prefetch-/Preload-I/O vermeiden; auf NVMe-Systemen ohnehin von geringem Nutzen.",
            "Kein Preloading — nach dem Flug automatisch wieder aktiv.", true),
        new("DiagTrack", "Telemetrie (Connected User Experiences)",
            "Telemetrie-Sammlung/-Upload pausieren.",
            "Keine Einschränkung im Alltag.", true),
        new("WerSvc", "Windows-Fehlerberichterstattung",
            "Fehlerbericht-Uploads pausieren.",
            "Absturzberichte anderer Programme werden nicht gesendet.", true),
        new("MapsBroker", "Heruntergeladene Karten",
            "Karten-Update-Dienst pausieren (für MSFS irrelevant).",
            "Windows-Karten-App aktualisiert nicht.", true),
        new("Spooler", "Druckwarteschlange",
            "Druckerdienst pausieren — nur sinnvoll, wenn während des Flugs sicher nicht gedruckt wird.",
            "Drucken ist während des Flugs NICHT möglich.", false),
        new("Fax", "Fax",
            "Faxdienst pausieren (falls überhaupt vorhanden).",
            "Kein Faxempfang (den vermisst vermutlich niemand).", false),
    };

    private static string StateDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Msfs2024AiTuner");
    public static string StatePath => Path.Combine(StateDir, "flightmode-state.json");
    public static string ConfigPath => Path.Combine(StateDir, "flightmode-config.json");

    /// <summary>Status eines Dienstes: "Läuft", "Gestoppt", "Nicht vorhanden".</summary>
    public static string GetStatus(string serviceName)
    {
        try
        {
            using var controller = new ServiceController(serviceName);
            return controller.Status switch
            {
                ServiceControllerStatus.Running => "Läuft",
                ServiceControllerStatus.StartPending => "Startet",
                ServiceControllerStatus.StopPending => "Stoppt",
                _ => "Gestoppt",
            };
        }
        catch
        {
            return "Nicht vorhanden";
        }
    }

    /// <summary>true, wenn ein Flug-Modus-Zustand aussteht (Dienste gestoppt, noch nicht wiederhergestellt).</summary>
    public static bool IsPauseActive => File.Exists(StatePath);

    public static List<string> LoadSelection()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(ConfigPath)) ?? DefaultSelection();
        }
        catch { }
        return DefaultSelection();
    }

    public static void SaveSelection(List<string> names)
    {
        try
        {
            Directory.CreateDirectory(StateDir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(names));
        }
        catch { }
    }

    private static List<string> DefaultSelection()
        => Catalog.Where(s => s.DefaultChecked).Select(s => s.Name).ToList();

    // ----- Ab hier: läuft im ELEVATED Helfer-Prozess -----

    /// <summary>Stoppt die gewählten Dienste, wartet auf das Sim-Ende und stellt alles wieder her.</summary>
    public static int RunFlightMode(string namesCsv)
    {
        var requested = namesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var allowed = new HashSet<string>(Catalog.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        var stopped = new List<string>();

        foreach (var name in requested.Where(n => allowed.Contains(n)))
        {
            try
            {
                using var controller = new ServiceController(name);
                if (controller.Status != ServiceControllerStatus.Running)
                    continue;
                controller.Stop();
                controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
                stopped.Add(name);
            }
            catch { }
        }

        Directory.CreateDirectory(StateDir);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(stopped));

        // Auf das Sim-Ende warten: läuft er noch nicht, bis zu 3 h auf den Start warten.
        var sawSim = WaitFor(() => MsfsApplier.IsSimRunning(), TimeSpan.FromHours(3));
        if (sawSim)
            WaitFor(() => !MsfsApplier.IsSimRunning(), TimeSpan.FromHours(24));

        return Resume();
    }

    /// <summary>Stellt die zuvor gestoppten Dienste wieder her (auch manuell/nach Absturz aufrufbar).</summary>
    public static int Resume()
    {
        try
        {
            if (!File.Exists(StatePath))
                return 0;
            var stopped = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(StatePath)) ?? new();
            var failed = 0;
            foreach (var name in stopped)
            {
                try
                {
                    using var controller = new ServiceController(name);
                    if (controller.Status == ServiceControllerStatus.Stopped)
                    {
                        controller.Start();
                        controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
                    }
                }
                catch { failed++; }
            }
            File.Delete(StatePath);
            return failed == 0 ? 0 : 2;
        }
        catch
        {
            return 1;
        }
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (condition())
                return true;
            Thread.Sleep(5000);
        }
        return condition();
    }
}
