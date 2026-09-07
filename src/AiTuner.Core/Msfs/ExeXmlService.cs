using System.Xml.Linq;

namespace AiTuner.Core.Msfs;

/// <summary>Ein Autostart-Eintrag aus der EXE.xml (Programme, die MSFS beim Start mitstartet).</summary>
public sealed record ExeXmlEntry(
    string Name,
    string Path,
    bool Disabled,
    string CommandLine,
    bool PathExists);

/// <summary>
/// Liest und bearbeitet die EXE.xml (Launch.Addon-Einträge) einer MSFS-Installation.
/// Deaktivieren setzt nur das Disabled-Flag — Einträge werden NIE gelöscht, vor jeder
/// Änderung entsteht ein Backup.
/// </summary>
public static class ExeXmlService
{
    public static string? GetPath(MsfsInstallation installation)
    {
        var dir = System.IO.Path.GetDirectoryName(installation.UserCfgPath);
        if (dir is null)
            return null;
        var path = System.IO.Path.Combine(dir, "EXE.xml");
        return File.Exists(path) ? path : null;
    }

    public static List<ExeXmlEntry> Load(MsfsInstallation installation)
    {
        var result = new List<ExeXmlEntry>();
        var path = GetPath(installation);
        if (path is null)
            return result;

        try
        {
            var document = XDocument.Load(path);
            foreach (var addon in document.Root?.Elements("Launch.Addon") ?? Enumerable.Empty<XElement>())
            {
                var exePath = addon.Element("Path")?.Value.Trim() ?? "";
                result.Add(new ExeXmlEntry(
                    Name: addon.Element("Name")?.Value.Trim() ?? "(ohne Name)",
                    Path: exePath,
                    Disabled: string.Equals(addon.Element("Disabled")?.Value.Trim(), "True", StringComparison.OrdinalIgnoreCase),
                    CommandLine: addon.Element("CommandLine")?.Value.Trim() ?? "",
                    PathExists: exePath.Length > 0 && File.Exists(exePath)));
            }
        }
        catch { }
        return result;
    }

    /// <summary>Setzt das Disabled-Flag eines Eintrags (identifiziert über den Namen).</summary>
    public static (bool Ok, string Message) SetDisabled(MsfsInstallation installation, string name, bool disabled)
    {
        var path = GetPath(installation);
        if (path is null)
            return (false, "EXE.xml nicht gefunden.");

        try
        {
            // Backup vor jeder Änderung — die EXE.xml wird von Add-on-Installern gepflegt.
            // WICHTIG: ReadAllBytes/WriteAllBytes statt File.Copy — die Store-LocalCache-Dateien
            // sind EFS-verschlüsselt, Copy scheitert an „Datei konnte nicht verschlüsselt werden“.
            var backupDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Msfs2024AiTuner", "backups");
            Directory.CreateDirectory(backupDir);
            File.WriteAllBytes(System.IO.Path.Combine(backupDir,
                $"EXE_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.xml"), File.ReadAllBytes(path));

            var document = XDocument.Load(path);
            var addon = (document.Root?.Elements("Launch.Addon") ?? Enumerable.Empty<XElement>())
                .FirstOrDefault(a => string.Equals(a.Element("Name")?.Value.Trim(), name, StringComparison.OrdinalIgnoreCase));
            if (addon is null)
                return (false, $"Eintrag „{name}“ nicht in der EXE.xml gefunden.");

            var disabledElement = addon.Element("Disabled");
            if (disabledElement is null)
                addon.AddFirst(new XElement("Disabled", disabled ? "True" : "False"));
            else
                disabledElement.Value = disabled ? "True" : "False";

            document.Save(path);
            return (true, disabled
                ? $"„{name}“ deaktiviert — startet ab dem nächsten Sim-Start nicht mehr mit (Eintrag bleibt erhalten)."
                : $"„{name}“ aktiviert — startet ab dem nächsten Sim-Start wieder mit.");
        }
        catch (Exception ex)
        {
            return (false, "EXE.xml konnte nicht geändert werden: " + ex.Message);
        }
    }
}
