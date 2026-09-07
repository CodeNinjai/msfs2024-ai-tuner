using System.Text;
using System.Text.RegularExpressions;

namespace AiTuner.Core.Msfs;

/// <summary>Ein Paket-Eintrag aus der Content.xml (Aktivierungszustand des Sim-Content-Managers).</summary>
public sealed record ContentPackage(string RawName, string Prefix, string PackageName, string ActiveState)
{
    public bool IsActive => ActiveState.Equals("Activated", StringComparison.OrdinalIgnoreCase);
    public bool IsUserDisabled => ActiveState.Equals("UserDisabled", StringComparison.OrdinalIgnoreCase);
    public bool IsSystemDisabled => ActiveState.Equals("SystemDisabled", StringComparison.OrdinalIgnoreCase);
    public bool IsCommunity => Prefix.StartsWith("community", StringComparison.OrdinalIgnoreCase);

    public string SourceLabel => Prefix.ToLowerInvariant() switch
    {
        "communityfs24" => "Community · FS24",
        "communityfs20" => "Community · FS20",
        "fs24" => "Official FS24",
        "fs20" => "Official FS20",
        "" => "?",
        _ => Prefix,
    };
}

/// <summary>
/// Liest und bearbeitet die Content.xml (LocalCache\CodeNinjai\Content.xml) — die Paket-
/// Aktivierungsliste des Content-Managers. Werte: Activated / UserDisabled (Nutzer) /
/// SystemDisabled (vom Sim stillgelegt, wird hier nie verändert). Änderungen nur bei
/// geschlossenem Sim (er schreibt die Datei beim Beenden neu).
/// </summary>
public static class ContentXmlService
{
    private static readonly string[] KnownPrefixes = { "communityfs24", "communityfs20", "fs24", "fs20" };

    public static string? GetPath(MsfsInstallation installation)
    {
        var dir = System.IO.Path.GetDirectoryName(installation.UserCfgPath);
        if (dir is null)
            return null;
        var path = System.IO.Path.Combine(dir, "CodeNinjai", "Content.xml");
        return File.Exists(path) ? path : null;
    }

    public static List<ContentPackage> Load(MsfsInstallation installation)
    {
        var result = new List<ContentPackage>();
        var path = GetPath(installation);
        if (path is null)
            return result;

        try
        {
            var text = ReadText(path, out _);
            foreach (Match match in Regex.Matches(text, @"<Package\s+name=""([^""]+)""\s+active=""([^""]+)"""))
            {
                var raw = match.Groups[1].Value;
                var prefix = KnownPrefixes.FirstOrDefault(p => raw.StartsWith(p + "-", StringComparison.OrdinalIgnoreCase)) ?? "";
                var packageName = prefix.Length > 0 ? raw[(prefix.Length + 1)..] : raw;
                result.Add(new ContentPackage(raw, prefix, packageName, match.Groups[2].Value));
            }
        }
        catch { }
        return result;
    }

    /// <summary>Aktivierungszustand je Community-Paketname (klein geschrieben) — für den ICAO-/Konflikt-Abgleich.</summary>
    public static Dictionary<string, string> LoadStatesByPackageName(MsfsInstallation installation)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in Load(installation).Where(p => p.IsCommunity))
            result[package.PackageName] = package.ActiveState;
        return result;
    }

    /// <summary>Setzt Activated bzw. UserDisabled für einen Eintrag. SystemDisabled wird nicht angefasst.</summary>
    public static (bool Ok, string Message) SetActive(MsfsInstallation installation, string rawName, bool activate)
    {
        if (Apply.MsfsApplier.IsSimRunning())
            return (false, "MSFS läuft — die Content.xml wird beim Beenden vom Sim überschrieben. Bitte den Sim zuerst schließen.");

        var path = GetPath(installation);
        if (path is null)
            return (false, "Content.xml nicht gefunden.");

        try
        {
            var text = ReadText(path, out var hadBom);

            var pattern = @"(<Package\s+name=""" + Regex.Escape(rawName) + @"""\s+active="")([^""]+)("")";
            var match = Regex.Match(text, pattern);
            if (!match.Success)
                return (false, $"Eintrag „{rawName}“ nicht in der Content.xml gefunden.");
            if (match.Groups[2].Value.Equals("SystemDisabled", StringComparison.OrdinalIgnoreCase))
                return (false, "Vom Sim stillgelegt (SystemDisabled) — das sollte im Sim selbst geklärt werden (meist veraltetes Paket).");

            // Backup (Bytes — LocalCache ist EFS-verschlüsselt, File.Copy scheitert daran).
            var backupDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Msfs2024AiTuner", "backups");
            Directory.CreateDirectory(backupDir);
            File.WriteAllBytes(System.IO.Path.Combine(backupDir,
                $"Content_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.xml"), File.ReadAllBytes(path));

            var replaced = Regex.Replace(text, pattern, m => m.Groups[1].Value + (activate ? "Activated" : "UserDisabled") + m.Groups[3].Value);
            WriteText(path, replaced, hadBom);

            return (true, activate
                ? $"„{rawName}“ aktiviert — der Sim lädt das Paket beim nächsten Start wieder."
                : $"„{rawName}“ deaktiviert (UserDisabled) — wie im Content-Manager abgewählt; jederzeit reaktivierbar.");
        }
        catch (Exception ex)
        {
            return (false, "Content.xml konnte nicht geändert werden: " + ex.Message);
        }
    }

    private static string ReadText(string path, out bool hadBom)
    {
        var bytes = File.ReadAllBytes(path);
        hadBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        return Encoding.UTF8.GetString(hadBom ? bytes[3..] : bytes);
    }

    private static void WriteText(string path, string text, bool withBom)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (withBom)
            bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(bytes).ToArray();
        File.WriteAllBytes(path, bytes);
    }
}
