using System.Diagnostics;
using System.Text;
using AiTuner.Core.Msfs;

namespace AiTuner.Core.Apply;

/// <summary>Writes MSFS UserCfg.opt values (structure-preserving). Refuses while the sim runs.</summary>
public static class MsfsApplier
{
    private static readonly string[] SimProcessNames = ["FlightSimulator2024", "FlightSimulator"];

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static bool IsSimRunning()
        => SimProcessNames.Any(name => Process.GetProcessesByName(name).Length > 0);

    /// <summary>Name of the running sim process (with .exe), e.g. for PresentMon.</summary>
    public static string? RunningSimProcessName()
    {
        foreach (var name in SimProcessNames)
        {
            if (Process.GetProcessesByName(name).Length > 0)
                return name + ".exe";
        }
        return null;
    }

    public static Process? RunningSimProcess()
        => SimProcessNames
            .SelectMany(Process.GetProcessesByName)
            .FirstOrDefault();

    public static (bool Ok, string Message) SetValue(MsfsInstallation installation, string sectionPath, string key, string value)
    {
        if (IsSimRunning())
            return (false, "MSFS läuft — Änderungen an UserCfg.opt würden beim Beenden des Sims überschrieben. Bitte zuerst den Sim schließen.");

        var document = installation.LoadUserCfg();
        if (document is null)
            return (false, "UserCfg.opt konnte nicht gelesen werden.");

        if (!document.SetValue(sectionPath, key, value))
            return (false, $"Eintrag {sectionPath}/{key} nicht in der UserCfg gefunden.");

        try
        {
            File.WriteAllText(installation.UserCfgPath, document.ToText(), Utf8NoBom);
            return (true, $"{key} = {value} gesetzt (wirkt beim nächsten Sim-Start).");
        }
        catch (Exception ex)
        {
            return (false, "Schreiben fehlgeschlagen: " + ex.Message);
        }
    }

    /// <summary>Restores a backed-up UserCfg.opt, keeping the CURRENT InstalledPackagesPath.</summary>
    public static (bool Ok, string Message) RestoreUserCfg(MsfsInstallation installation, string backupFile)
    {
        if (IsSimRunning())
            return (false, "MSFS läuft — bitte zuerst den Sim schließen.");
        if (!File.Exists(backupFile))
            return (false, "Backup-Datei nicht gefunden.");

        try
        {
            var currentPackagesPath = installation.LoadUserCfg()?.GetValue("", "InstalledPackagesPath");

            var restored = UserCfgDocument.Parse(File.ReadAllText(backupFile));
            if (currentPackagesPath is not null)
                restored.SetValue("", "InstalledPackagesPath", currentPackagesPath);

            File.WriteAllText(installation.UserCfgPath, restored.ToText(), Utf8NoBom);
            return (true, "UserCfg.opt wiederhergestellt (InstalledPackagesPath beibehalten).");
        }
        catch (Exception ex)
        {
            return (false, "Wiederherstellen fehlgeschlagen: " + ex.Message);
        }
    }
}
