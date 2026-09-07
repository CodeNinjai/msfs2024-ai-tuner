using System.Text.Json;
using AiTuner.Core.Apply;
using AiTuner.Core.Msfs;

namespace AiTuner.Core.Libraries;

public sealed record LibraryInfo(string Path, bool AutoDetected, string Name,
    string ManagerKind = "") // z.B. "Aerosoft One" / "FSDT Addon Manager" — verwaltete Struktur, nie umbauen
{
    public bool Managed => ManagerKind.Length > 0;
}

public sealed record LibraryConfigEntry(string Path, string? Name);

public sealed record LibraryAddon(
    string FolderName,
    string Title,
    string Version,
    string Location,      // "Community", "Community2024" oder Bibliothekspfad
    string Path,
    bool IsLink,          // Community-Eintrag ist Junction/Symlink
    bool IsActive,        // Bibliothekseintrag: per Link in einem Community-Ordner aktiv
    bool IsBroken = false, // Link, dessen Ziel nicht mehr existiert
    bool IsNested = false, // Add-on liegt eine Ordnerebene tiefer in der Bibliothek
    string ActiveIn = "",  // Label des Community-Ordners, in dem der Aktivierungs-Link liegt
    string ContentType = "") // content_type aus der manifest.json (SCENERY, AIRCRAFT, LIVERY, …)
{
    private static readonly HashSet<string> AircraftTypes = new(StringComparer.OrdinalIgnoreCase)
        { "AIRCRAFT", "LIVERY", "LIVERIES", "INSTRUMENTS", "SOUND", "EFFECT", "SIMOBJECT" };

    /// <summary>Flugzeug/Livery &amp; Co. — keine Szenerie (eigene Gruppe, kein GSX-Chip).</summary>
    public bool IsAircraftish => AircraftTypes.Contains(ContentType);
}

public sealed class LibraryScanResult
{
    public IReadOnlyList<LibraryInfo> Libraries { get; init; } = [];
    public IReadOnlyList<LibraryAddon> CommunityItems { get; init; } = [];
    public IReadOnlyList<LibraryAddon> LibraryItems { get; init; } = [];
    public IReadOnlyList<string> Duplicates { get; init; } = [];
    public string? CommunityPath { get; init; }
    /// <summary>Seit SU4: Ordner nur für native 2024-Add-ons (wird von MSFS 2020 ignoriert). null wenn nicht vorhanden.</summary>
    public string? Community2024Path { get; init; }
}

/// <summary>
/// Szenerie-Bibliotheken: Add-ons außerhalb des Community-Ordners lagern und per
/// NTFS-Junction aktivieren/deaktivieren (keine Adminrechte nötig, MSFS-kompatibel).
/// </summary>
public static class LibraryService
{
    private static string ConfigPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Msfs2024AiTuner", "libraries.json");

    /// <summary>Lädt die Bibliotheks-Konfiguration; unterstützt das alte Format (reine Pfad-Liste).</summary>
    public static List<LibraryConfigEntry> LoadConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath))
                return new List<LibraryConfigEntry>();
            var json = File.ReadAllText(ConfigPath).TrimStart();
            if (json.StartsWith("[") && json.Contains("\"Path\""))
                return JsonSerializer.Deserialize<List<LibraryConfigEntry>>(json) ?? new();
            // Altformat: ["pfad1", "pfad2"]
            var paths = JsonSerializer.Deserialize<List<string>>(json) ?? new();
            return paths.Select(p => new LibraryConfigEntry(p, null)).ToList();
        }
        catch
        {
            return new List<LibraryConfigEntry>();
        }
    }

    public static void SaveConfig(IEnumerable<LibraryConfigEntry> entries)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ConfigPath)!);
        var distinct = entries
            .GroupBy(e => System.IO.Path.TrimEndingDirectorySeparator(e.Path), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(distinct, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static List<string> LoadConfiguredLibraries()
        => LoadConfig().Select(e => e.Path).ToList();

    /// <summary>Vergibt/ändert den Anzeigenamen; automatisch erkannte Bibliotheken werden dadurch fest übernommen.</summary>
    public static void RenameLibrary(string path, string newName)
    {
        var config = LoadConfig();
        var index = config.FindIndex(e => string.Equals(
            System.IO.Path.TrimEndingDirectorySeparator(e.Path),
            System.IO.Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase));
        var entry = new LibraryConfigEntry(path, string.IsNullOrWhiteSpace(newName) ? null : newName.Trim());
        if (index >= 0)
            config[index] = entry;
        else
            config.Add(entry);
        SaveConfig(config);
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(System.IO.Path.TrimEndingDirectorySeparator(a),
                         System.IO.Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Erkennt Addon-Manager-Strukturen am Link-Ziel: Aerosoft One legt pro Produkt
    /// „…\Add-ons\&lt;produkt&gt;\gameDirectory~Community\&lt;paket&gt;“ an — alle Produkte werden zu EINER
    /// verwalteten Bibliothek (Wurzel = Ordner über „Add-ons“) gruppiert. FSDT Addon Manager
    /// („…\Addon Manager\MSFS“) wird als verwaltet markiert. Verwaltete Bibliotheken nie umbauen!
    /// </summary>
    private static (string Root, string ManagerKind) ResolveManagedRoot(string parent)
    {
        var trimmed = System.IO.Path.TrimEndingDirectorySeparator(parent);
        var leaf = System.IO.Path.GetFileName(trimmed);

        if (leaf.Equals("gameDirectory~Community", StringComparison.OrdinalIgnoreCase))
        {
            var product = System.IO.Path.GetDirectoryName(trimmed);
            var addons = product is null ? null : System.IO.Path.GetDirectoryName(product);
            if (addons is not null
                && System.IO.Path.GetFileName(addons).Equals("Add-ons", StringComparison.OrdinalIgnoreCase)
                && System.IO.Path.GetDirectoryName(addons) is { } managerRoot)
                return (managerRoot, "Aerosoft One");
        }

        var parentDir = System.IO.Path.GetDirectoryName(trimmed);
        if (parentDir is not null
            && System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(parentDir))
                .Equals("Addon Manager", StringComparison.OrdinalIgnoreCase))
            return (trimmed, "FSDT Addon Manager");

        return (trimmed, "");
    }

    /// <summary>Add-on-Ordner einer Bibliothek — bei Aerosoft One über die Produkt-Struktur.</summary>
    private static IEnumerable<string> EnumerateLibraryAddonDirs(LibraryInfo library)
    {
        if (library.ManagerKind == "Aerosoft One")
        {
            var addonsDir = System.IO.Path.Combine(library.Path, "Add-ons");
            if (!Directory.Exists(addonsDir))
                yield break;
            foreach (var product in Directory.EnumerateDirectories(addonsDir))
            {
                var gameDir = System.IO.Path.Combine(product, "gameDirectory~Community");
                if (!Directory.Exists(gameDir))
                    continue;
                foreach (var dir in Directory.EnumerateDirectories(gameDir))
                    yield return dir;
            }
            yield break;
        }

        foreach (var dir in Directory.EnumerateDirectories(library.Path))
            yield return dir;
    }

    private static string DefaultLibraryName(string path)
        => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } leaf ? leaf : path;

    public static string? GetCommunityPath(MsfsInstallation installation)
    {
        var packagesPath = AddonScanner.GetInstalledPackagesPath(installation);
        if (packagesPath is null)
            return null;
        var community = System.IO.Path.Combine(packagesPath, "Community");
        return Directory.Exists(community) ? community : null;
    }

    /// <summary>Community2024 (seit SU4, nur MSFS 2024) — Geschwisterordner von Community.</summary>
    public static string? GetCommunity2024Path(MsfsInstallation installation)
    {
        var packagesPath = AddonScanner.GetInstalledPackagesPath(installation);
        if (packagesPath is null)
            return null;
        var community2024 = System.IO.Path.Combine(packagesPath, "Community2024");
        return Directory.Exists(community2024) ? community2024 : null;
    }

    public static LibraryScanResult Scan(MsfsInstallation installation)
    {
        var communityPath = GetCommunityPath(installation);
        if (communityPath is null)
            return new LibraryScanResult();

        var community2024Path = GetCommunity2024Path(installation);
        var configEntries = LoadConfig();
        var configured = configEntries.Select(e => e.Path).ToList();
        var communityItems = new List<LibraryAddon>();
        // FolderName -> (Zielpfad, Community-Wurzel-Label des Links)
        var linkTargets = new Dictionary<string, (string Target, string Root)>(StringComparer.OrdinalIgnoreCase);
        var detectedLibraries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // Pfad -> ManagerKind ("" = normal)

        var roots = new List<(string Label, string Path)> { ("Community", communityPath) };
        if (community2024Path is not null)
            roots.Add(("Community2024", community2024Path));

        foreach (var (rootLabel, rootPath) in roots)
        foreach (var dir in Directory.EnumerateDirectories(rootPath))
        {
            var info = new DirectoryInfo(dir);
            var isLink = (info.Attributes & FileAttributes.ReparsePoint) != 0;
            string? target = null;
            var broken = false;
            if (isLink)
            {
                try { target = info.LinkTarget; } catch { }
                // Manche Junctions tragen ein abschließendes "\" im Ziel — normalisieren,
                // sonst liefert GetDirectoryName den Add-on-Ordner selbst als "Bibliothek".
                if (target is not null)
                    target = System.IO.Path.TrimEndingDirectorySeparator(target);
                broken = target is null || !Directory.Exists(target);
                if (target is not null && !broken)
                {
                    linkTargets[info.Name] = (target, rootLabel);
                    var parent = System.IO.Path.GetDirectoryName(target);
                    if (parent is not null)
                    {
                        var (libraryRoot, managerKind) = ResolveManagedRoot(parent);
                        if (!configured.Contains(libraryRoot, StringComparer.OrdinalIgnoreCase))
                            detectedLibraries[libraryRoot] = managerKind;
                    }
                }
            }

            var (title, version, contentType) = ReadManifestFull(dir);
            communityItems.Add(new LibraryAddon(info.Name, title, version, rootLabel, dir, isLink,
                IsActive: !broken, IsBroken: broken, ActiveIn: rootLabel, ContentType: contentType));
        }

        var libraries = configEntries
            .Select(e => new LibraryInfo(e.Path, AutoDetected: false, e.Name ?? DefaultLibraryName(e.Path)))
            .Concat(detectedLibraries.Select(kv => new LibraryInfo(kv.Key, AutoDetected: true,
                kv.Value.Length > 0 ? kv.Value : DefaultLibraryName(kv.Key), ManagerKind: kv.Value)))
            .Where(l => Directory.Exists(l.Path))
            .ToList();

        // Unter-Bibliotheken (z.B. E:\Scenery\fs24 unter E:\Scenery) bleiben EIGENSTÄNDIG —
        // die übergeordnete Bibliothek überspringt solche Ordner beim Scannen, damit
        // nichts doppelt gezählt wird, die bewusste Struktur aber erhalten bleibt.
        var libraryPathSet = new HashSet<string>(
            libraries.Select(l => System.IO.Path.TrimEndingDirectorySeparator(l.Path)),
            StringComparer.OrdinalIgnoreCase);

        var libraryItems = new List<LibraryAddon>();
        foreach (var library in libraries)
        {
            foreach (var dir in EnumerateLibraryAddonDirs(library))
            {
                // Ordner, die selbst als Bibliothek geführt werden, gehören nicht zur Eltern-Bibliothek.
                if (libraryPathSet.Contains(System.IO.Path.TrimEndingDirectorySeparator(dir)))
                    continue;

                if (File.Exists(System.IO.Path.Combine(dir, "manifest.json")))
                {
                    libraryItems.Add(BuildLibraryAddon(library.Path, dir, linkTargets, nested: false));
                    continue;
                }

                // Verschachtelte Struktur: manifest.json eine Ebene tiefer (z.B. entpackte ZIPs).
                try
                {
                    foreach (var sub in Directory.EnumerateDirectories(dir))
                    {
                        if (File.Exists(System.IO.Path.Combine(sub, "manifest.json")))
                            libraryItems.Add(BuildLibraryAddon(library.Path, sub, linkTargets, nested: true));
                    }
                }
                catch { }
            }
        }

        // Sicherheitsnetz: identische physische Pfade nie doppelt listen.
        libraryItems = libraryItems
            .GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        // Dubletten: derselbe Paketordner an mehreren Orten (echt in Community + Bibliothek, oder in 2 Bibliotheken)
        var duplicates = new List<string>();

        // Gleicher Name in Community UND Community2024 (egal ob Link oder fest):
        // MSFS 2024 lädt BEIDE Ordner — das Paket wäre doppelt geladen.
        foreach (var group in communityItems
            .GroupBy(c => c.FolderName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(c => c.Location).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
        {
            duplicates.Add($"„{group.Key}“ liegt in Community UND Community2024 — MSFS 2024 lädt beide Ordner, das Paket wird DOPPELT geladen. Eine Kopie entfernen/deaktivieren.");
        }

        var communityRootLabels = new HashSet<string>(new[] { "Community", "Community2024" }, StringComparer.OrdinalIgnoreCase);
        var byName = communityItems.Where(c => !c.IsLink).Select(c => (c.FolderName, Ort: c.Location, c.Version))
            .Concat(libraryItems.Select(l => (l.FolderName, Ort: l.Location, l.Version)))
            .GroupBy(x => x.FolderName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(x => x.Ort).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            // Reine Community↔Community2024-Paare sind oben schon als Doppel-Lade-Warnung gemeldet.
            .Where(g => !g.All(x => communityRootLabels.Contains(x.Ort)));
        foreach (var group in byName)
            duplicates.Add($"„{group.Key}“ liegt mehrfach vor: " +
                string.Join("  ·  ", group.Select(x => $"{x.Ort} (v{(string.IsNullOrEmpty(x.Version) ? "?" : x.Version)})")));

        return new LibraryScanResult
        {
            Libraries = libraries,
            CommunityItems = communityItems,
            LibraryItems = libraryItems,
            Duplicates = duplicates,
            CommunityPath = communityPath,
            Community2024Path = community2024Path,
        };
    }

    /// <summary>Aktiviert ein Bibliotheks-Add-on per Junction im Community-Ordner.</summary>
    public static (bool Ok, string Message) Activate(string communityPath, LibraryAddon addon)
    {
        if (MsfsApplier.IsSimRunning())
            return (false, "MSFS läuft — Add-on-Änderungen wirken erst nach Neustart. Bitte den Sim zuerst schließen.");

        var linkPath = System.IO.Path.Combine(communityPath, addon.FolderName);
        if (Directory.Exists(linkPath))
            return (false, $"Im Community-Ordner existiert bereits „{addon.FolderName}“.");

        var output = ProcessRunner.Run("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{addon.Path}\"");
        return Directory.Exists(linkPath)
            ? (true, $"„{addon.FolderName}“ aktiviert (Junction).")
            : (false, "Junction konnte nicht erstellt werden: " + (output ?? "unbekannter Fehler"));
    }

    /// <summary>Deaktiviert ein verlinktes Add-on (löscht NUR die Junction, nie den Inhalt).</summary>
    public static (bool Ok, string Message) Deactivate(string communityPath, string folderName)
    {
        if (MsfsApplier.IsSimRunning())
            return (false, "MSFS läuft — bitte den Sim zuerst schließen.");

        var linkPath = System.IO.Path.Combine(communityPath, folderName);
        var info = new DirectoryInfo(linkPath);
        // Achtung: Bei defekten Junctions (Ziel fehlt) liefert Exists=false — Attributes funktionieren trotzdem.
        FileAttributes attributes;
        try { attributes = info.Attributes; } catch { return (false, "Eintrag nicht gefunden."); }
        if ((int)attributes == -1)
            return (false, "Eintrag nicht gefunden.");
        if ((attributes & FileAttributes.ReparsePoint) == 0)
            return (false, "Sicherheitsstopp: Dieser Eintrag ist ein echter Ordner, kein Link — er wird nicht gelöscht.");

        try
        {
            Directory.Delete(linkPath, recursive: false);
            return (true, $"„{folderName}“ deaktiviert (Link entfernt, Inhalt bleibt in der Bibliothek).");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Universelles Verschieben eines Add-ons: Community(-2024)-fest ↔ Bibliothek ↔ andere Bibliothek.
    /// targetLibrary = null bedeutet „fest in einen Community-Ordner“ — welcher, bestimmt
    /// fixedTargetRoot (Default: klassischer Community-Ordner). community2024Path (falls vorhanden)
    /// wird für Link-Suche/-Neuanlage mit berücksichtigt.
    /// </summary>
    public static (bool Ok, string Message) MoveAddon(string communityPath, LibraryAddon addon, string? targetLibrary,
        bool keepActive, string? community2024Path = null, string? fixedTargetRoot = null)
    {
        if (MsfsApplier.IsSimRunning())
            return (false, "MSFS läuft — bitte den Sim zuerst schließen.");

        var isCommunityFixed = !addon.IsLink &&
            (addon.Location.Equals("Community", StringComparison.OrdinalIgnoreCase) ||
             addon.Location.Equals("Community2024", StringComparison.OrdinalIgnoreCase));

        var roots = new List<string> { communityPath };
        if (community2024Path is not null)
            roots.Add(community2024Path);

        // Vorhandenen Aktivierungs-Link (in egal welcher Wurzel) finden.
        string? existingLinkPath = null;
        foreach (var root in roots)
        {
            var candidate = System.IO.Path.Combine(root, addon.FolderName);
            var info = new DirectoryInfo(candidate);
            try
            {
                if ((int)info.Attributes != -1 && (info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    existingLinkPath = candidate;
                    break;
                }
            }
            catch { }
        }

        try
        {
            if (targetLibrary is null)
            {
                // Ziel: fest in einen Community-Ordner
                var targetRoot = fixedTargetRoot ?? communityPath;
                var targetRootName = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(targetRoot));
                var destination = System.IO.Path.Combine(targetRoot, addon.FolderName);

                if (isCommunityFixed && PathsEqual(System.IO.Path.GetDirectoryName(addon.Path) ?? "", targetRoot))
                    return (false, $"Liegt bereits fest im {targetRootName}-Ordner.");

                if (existingLinkPath is not null)
                    Directory.Delete(existingLinkPath, recursive: false);
                if (Directory.Exists(destination))
                    return (false, $"Im {targetRootName}-Ordner existiert bereits ein echter Ordner „{addon.FolderName}“.");

                MoveDirectoryRobust(addon.Path, destination);
                return (true, $"„{addon.FolderName}“ in den {targetRootName}-Ordner verschoben (fest installiert, aktiv).");
            }

            // Ziel: eine Bibliothek
            var target = System.IO.Path.Combine(targetLibrary, addon.FolderName);
            if (Directory.Exists(target))
                return (false, $"In der Ziel-Bibliothek existiert bereits „{addon.FolderName}“.");
            Directory.CreateDirectory(targetLibrary);

            var wasActive = isCommunityFixed || existingLinkPath is not null;
            // Re-Link in dieselbe Wurzel, in der das Add-on bisher lag/verlinkt war.
            var relinkPath = existingLinkPath
                ?? System.IO.Path.Combine(isCommunityFixed
                    ? System.IO.Path.GetDirectoryName(addon.Path) ?? communityPath
                    : communityPath, addon.FolderName);

            if (existingLinkPath is not null)
                Directory.Delete(existingLinkPath, recursive: false);
            MoveDirectoryRobust(addon.Path, target);

            if (wasActive && keepActive)
            {
                ProcessRunner.Run("cmd.exe", $"/c mklink /J \"{relinkPath}\" \"{target}\"");
                if (!Directory.Exists(relinkPath))
                    return (false, $"Verschoben nach {targetLibrary}, aber Re-Link fehlgeschlagen — Add-on ist jetzt DEAKTIVIERT.");
                return (true, $"„{addon.FolderName}“ nach {targetLibrary} verschoben — bleibt aktiv (Link).");
            }

            return (true, wasActive
                ? $"„{addon.FolderName}“ nach {targetLibrary} verschoben und DEAKTIVIERT (kein Link)."
                : $"„{addon.FolderName}“ nach {targetLibrary} verschoben (bleibt inaktiv).");
        }
        catch (Exception ex)
        {
            return (false, "Verschieben fehlgeschlagen: " + ex.Message);
        }
    }

    /// <summary>Lagert einen echten Community-Ordner in eine Bibliothek aus und verlinkt ihn zurück.</summary>
    public static (bool Ok, string Message) MoveToLibrary(string communityPath, string folderName, string libraryPath)
    {
        if (MsfsApplier.IsSimRunning())
            return (false, "MSFS läuft — bitte den Sim zuerst schließen.");

        var source = System.IO.Path.Combine(communityPath, folderName);
        var sourceInfo = new DirectoryInfo(source);
        if (!sourceInfo.Exists)
            return (false, "Quellordner nicht gefunden.");
        if ((sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            return (false, "Bereits verlinkt — nichts auszulagern.");

        var target = System.IO.Path.Combine(libraryPath, folderName);
        if (Directory.Exists(target))
            return (false, $"In der Bibliothek existiert bereits „{folderName}“.");

        try
        {
            try
            {
                Directory.Move(source, target);
            }
            catch (IOException)
            {
                // Laufwerksübergreifend: kopieren + löschen.
                CopyDirectory(source, target);
                Directory.Delete(source, recursive: true);
            }
        }
        catch (Exception ex)
        {
            return (false, "Verschieben fehlgeschlagen: " + ex.Message);
        }

        var output = ProcessRunner.Run("cmd.exe", $"/c mklink /J \"{source}\" \"{target}\"");
        return Directory.Exists(source)
            ? (true, $"„{folderName}“ in die Bibliothek verschoben und verlinkt.")
            : (false, "Verschoben, aber Junction fehlgeschlagen: " + (output ?? "unbekannt") +
                      $" — Add-on liegt jetzt unter {target} und ist DEAKTIVIERT.");
    }

    private static LibraryAddon BuildLibraryAddon(string libraryPath, string addonDir,
        Dictionary<string, (string Target, string Root)> linkTargets, bool nested)
    {
        var name = System.IO.Path.GetFileName(addonDir);
        var active = linkTargets.TryGetValue(name, out var link)
            && string.Equals(System.IO.Path.TrimEndingDirectorySeparator(link.Target),
                             System.IO.Path.TrimEndingDirectorySeparator(addonDir), StringComparison.OrdinalIgnoreCase);
        var (title, version, contentType) = ReadManifestFull(addonDir);
        return new LibraryAddon(name, title, version, libraryPath, addonDir, IsLink: false, active,
            IsNested: nested, ActiveIn: active ? link.Root : "", ContentType: contentType);
    }

    /// <summary>Community-Junctions, die in die angegebene Bibliothek zeigen (Name → Linkpfad).</summary>
    private static Dictionary<string, string> GetJunctionsInto(string communityPath, string libraryPath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var prefix = System.IO.Path.TrimEndingDirectorySeparator(libraryPath) + System.IO.Path.DirectorySeparatorChar;
        foreach (var dir in Directory.EnumerateDirectories(communityPath))
        {
            var info = new DirectoryInfo(dir);
            if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
                continue;
            string? target = null;
            try { target = info.LinkTarget; } catch { }
            if (target is not null
                && System.IO.Path.TrimEndingDirectorySeparator(target).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                result[info.Name] = dir;
        }
        return result;
    }

    internal static void MoveDirectoryRobust(string source, string target)
    {
        try
        {
            Directory.Move(source, target);
        }
        catch (IOException)
        {
            CopyDirectory(source, target);
            Directory.Delete(source, recursive: true);
        }
    }

    public static int CountAddons(string libraryPath)
    {
        try
        {
            return Directory.EnumerateDirectories(libraryPath)
                .Count(d => File.Exists(System.IO.Path.Combine(d, "manifest.json")));
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Migriert alle Add-ons einer Bibliothek in einen anderen Ordner. Aktive Junctions
    /// im Community-Ordner werden automatisch auf den neuen Ort umgebogen.
    /// </summary>
    public static (bool Ok, List<string> Log) MigrateLibrary(string communityPath, string sourceLibrary, string targetLibrary,
        string? community2024Path = null)
    {
        var log = new List<string>();
        if (MsfsApplier.IsSimRunning())
            return (false, new List<string> { "MSFS läuft — bitte den Sim zuerst schließen." });
        if (string.Equals(System.IO.Path.TrimEndingDirectorySeparator(sourceLibrary),
                System.IO.Path.TrimEndingDirectorySeparator(targetLibrary), StringComparison.OrdinalIgnoreCase))
            return (false, new List<string> { "Quelle und Ziel sind identisch." });

        Directory.CreateDirectory(targetLibrary);
        var junctions = GetJunctionsInto(communityPath, sourceLibrary);
        if (community2024Path is not null)
            foreach (var pair in GetJunctionsInto(community2024Path, sourceLibrary))
                junctions.TryAdd(pair.Key, pair.Value);
        var allOk = true;

        foreach (var dir in Directory.EnumerateDirectories(sourceLibrary).ToList())
        {
            var name = System.IO.Path.GetFileName(dir);
            if (!File.Exists(System.IO.Path.Combine(dir, "manifest.json")))
            {
                log.Add($"⏭ „{name}“ übersprungen (kein Add-on).");
                continue;
            }

            var target = System.IO.Path.Combine(targetLibrary, name);
            if (Directory.Exists(target))
            {
                log.Add($"✗ „{name}“: existiert bereits im Ziel — übersprungen.");
                allOk = false;
                continue;
            }

            var wasActive = junctions.TryGetValue(name, out var linkPath);
            try
            {
                if (wasActive)
                    Directory.Delete(linkPath!, recursive: false);
                MoveDirectoryRobust(dir, target);
                if (wasActive)
                {
                    ProcessRunner.Run("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{target}\"");
                    var relinked = Directory.Exists(linkPath!);
                    log.Add(relinked
                        ? $"✓ „{name}“ migriert, Link umgebogen (bleibt aktiv)."
                        : $"✗ „{name}“ migriert, aber Re-Link fehlgeschlagen — jetzt DEAKTIVIERT.");
                    allOk &= relinked;
                }
                else
                {
                    log.Add($"✓ „{name}“ migriert.");
                }
            }
            catch (Exception ex)
            {
                log.Add($"✗ „{name}“: {ex.Message}");
                allOk = false;
            }
        }

        var config = LoadConfig();
        var sourceEntry = config.FirstOrDefault(e => PathsEqual(e.Path, sourceLibrary));
        if (!config.Any(e => PathsEqual(e.Path, targetLibrary)))
            config.Add(new LibraryConfigEntry(targetLibrary, sourceEntry?.Name));
        if (CountAddons(sourceLibrary) == 0)
        {
            config.RemoveAll(e => PathsEqual(e.Path, sourceLibrary));
            log.Add("Quell-Bibliothek ist leer und wurde aus der Liste entfernt.");
        }
        SaveConfig(config);

        return (allOk, log);
    }

    /// <summary>
    /// Löst eine Bibliothek auf: alle Add-ons zurück in den Community-Ordner (als echte Ordner).
    /// Vorher inaktive Add-ons werden dadurch aktiv.
    /// </summary>
    public static (bool Ok, List<string> Log) DissolveToCommunity(string communityPath, string sourceLibrary,
        string? community2024Path = null)
    {
        var log = new List<string>();
        if (MsfsApplier.IsSimRunning())
            return (false, new List<string> { "MSFS läuft — bitte den Sim zuerst schließen." });

        var junctions = GetJunctionsInto(communityPath, sourceLibrary);
        if (community2024Path is not null)
            foreach (var pair in GetJunctionsInto(community2024Path, sourceLibrary))
                junctions.TryAdd(pair.Key, pair.Value);
        var allOk = true;

        foreach (var dir in Directory.EnumerateDirectories(sourceLibrary).ToList())
        {
            var name = System.IO.Path.GetFileName(dir);
            if (!File.Exists(System.IO.Path.Combine(dir, "manifest.json")))
            {
                log.Add($"⏭ „{name}“ übersprungen (kein Add-on).");
                continue;
            }

            try
            {
                if (junctions.TryGetValue(name, out var linkPath))
                    Directory.Delete(linkPath, recursive: false);

                var target = System.IO.Path.Combine(communityPath, name);
                if (Directory.Exists(target))
                {
                    log.Add($"✗ „{name}“: existiert bereits im Community-Ordner — übersprungen.");
                    allOk = false;
                    continue;
                }

                MoveDirectoryRobust(dir, target);
                log.Add($"✓ „{name}“ zurück in den Community-Ordner (aktiv).");
            }
            catch (Exception ex)
            {
                log.Add($"✗ „{name}“: {ex.Message}");
                allOk = false;
            }
        }

        if (CountAddons(sourceLibrary) == 0)
        {
            var config = LoadConfig();
            config.RemoveAll(e => PathsEqual(e.Path, sourceLibrary));
            SaveConfig(config);
            log.Add("Bibliothek ist leer und wurde aus der Liste entfernt.");
        }

        return (allOk, log);
    }

    internal static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = System.IO.Path.GetRelativePath(source, file);
            var destination = System.IO.Path.Combine(target, relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    internal static (string Title, string Version) ReadManifest(string dir)
    {
        var (title, version, _) = ReadManifestFull(dir);
        return (title, version);
    }

    internal static (string Title, string Version, string ContentType) ReadManifestFull(string dir)
    {
        try
        {
            var manifestPath = System.IO.Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath))
                return (System.IO.Path.GetFileName(dir), "", "");
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = manifest.RootElement;
            var title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var version = root.TryGetProperty("package_version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var contentType = root.TryGetProperty("content_type", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            return (title ?? System.IO.Path.GetFileName(dir), version ?? "", contentType ?? "");
        }
        catch
        {
            return (System.IO.Path.GetFileName(dir), "", "");
        }
    }
}
