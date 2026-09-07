using System.Text.RegularExpressions;

namespace AiTuner.Core.Libraries;

/// <summary>Eine installierbare Profilvariante (z.B. „nool VDGS“-Fassung) — ini + zugehörige py-Dateien.</summary>
public sealed record GsxProfileVariant(string Label, string Vdgs, IReadOnlyList<string> Files);

/// <summary>Alle gefundenen Varianten eines GSX-Profils für einen Airport.</summary>
public sealed record GsxProfileCandidate(string Icao, List<GsxProfileVariant> Variants);

/// <summary>GSX-Aircraft-Configs (z.B. Fenix-Flotte): Ordner unter „…\Airplanes\&lt;Muster&gt;\“ mit GSX-*.cfg-Dateien.</summary>
public sealed record GsxAircraftConfig(string AircraftFolder, string SourceDir, int FileCount);

/// <summary>
/// Erkennt eine GSX-Installation (FSDT) und liest die vorhandenen Airport-Profile.
/// Profile liegen in %APPDATA%\virtuali\GSX\MSFS als "&lt;ICAO&gt;[-_ ]Rest.ini" (+ optionale .py-Skripte).
/// </summary>
public static class GsxProfileService
{
    public static string ProfilesPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "virtuali", "GSX", "MSFS");

    public static bool IsGsxInstalled => Directory.Exists(ProfilesPath);

    /// <summary>ICAO (Großschreibung) → Profildateien, die mit diesem ICAO beginnen.</summary>
    public static Dictionary<string, List<string>> LoadProfilesByIcao()
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!IsGsxInstalled)
                return result;

            foreach (var file in Directory.EnumerateFiles(ProfilesPath))
            {
                var name = Path.GetFileName(file);
                var ext = Path.GetExtension(name).ToLowerInvariant();
                if (ext is not (".ini" or ".py"))
                    continue;

                // ICAO = führende 4 Zeichen (Buchstabe + 3 alphanumerisch), danach Trenner oder Dateiende.
                var match = Regex.Match(name, @"^([A-Za-z][A-Za-z0-9]{3})(?=[\s\-_.]|$)");
                if (!match.Success)
                    continue;

                var icao = match.Groups[1].Value.ToUpperInvariant();
                if (!result.TryGetValue(icao, out var list))
                    result[icao] = list = new List<string>();
                list.Add(name);
            }
        }
        catch { }
        return result;
    }

    private static readonly Regex IcaoPrefix = new(@"^([A-Za-z][A-Za-z0-9]{3})(?=[\s\-_.]|$)", RegexOptions.Compiled);

    /// <summary>
    /// VDGS-Geschmacksrichtung aus Pfad/Dateiname erkennen: GSX-eigenes VDGS („GS(X) VDGS“),
    /// Aerosoft VDGS („AS VDGS“) oder nool VDGS (vdgs.nool.ee). Ohne Marker: „Standard“.
    /// </summary>
    public static string DetectVdgsFlavor(string pathOrName)
    {
        var s = pathOrName.ToLowerInvariant();
        if (s.Contains("nool"))
            return "nool VDGS";
        // WICHTIG: nur VDGS-gebundene Marker werten — „Aerosoft“ allein ist meist der
        // Scenery-Entwickler (EDDB-Aerosoft-GSX.ini), nicht die VDGS-Fassung!
        if (Regex.IsMatch(s, @"\b(as|aerosoft)[\s_-]*vdgs"))
            return "Aerosoft VDGS";
        if (Regex.IsMatch(s, @"\b(gsx|gs)[\s_-]*vdgs") || s.Contains("vdgs"))
            return "GSX VDGS";
        return "Standard";
    }

    /// <summary>Welche VDGS-Add-ons sind installiert? (nool / Aerosoft; das GSX-eigene ist immer da.)</summary>
    public static (bool HasNool, bool HasAerosoftVdgs) DetectInstalledVdgs(IEnumerable<string> packageFolderNames)
    {
        bool nool = false, aerosoft = false;
        foreach (var name in packageFolderNames)
        {
            var s = name.ToLowerInvariant();
            if (s.Contains("nool"))
                nool = true;
            if (s.Contains("vdgs") && (s.Contains("aerosoft") || s.Contains("as-")))
                aerosoft = true;
        }
        return (nool, aerosoft);
    }

    /// <summary>
    /// Sucht GSX-Profile (ICAO-benannte .ini) in einem Quellordner — rekursiv, inklusive
    /// Varianten in Unterordnern oder mit unterschiedlichen Dateinamen. Pfade unterhalb
    /// von excludeRoots (erkannte MSFS-Pakete) werden übersprungen.
    /// </summary>
    public static List<GsxProfileCandidate> FindProfiles(string root, IReadOnlyList<string>? excludeRoots = null,
        string? fallbackContext = null)
    {
        var variants = new List<(string Icao, GsxProfileVariant Variant)>();
        try
        {
            var excludes = (excludeRoots ?? Array.Empty<string>())
                .Select(e => Path.TrimEndingDirectorySeparator(Path.GetFullPath(e)) + Path.DirectorySeparatorChar)
                .ToList();

            foreach (var ini in Directory.EnumerateFiles(root, "*.ini", SearchOption.AllDirectories))
            {
                var fullPath = Path.GetFullPath(ini);
                if (excludes.Any(e => fullPath.StartsWith(e, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var fileName = Path.GetFileName(ini);
                var match = IcaoPrefix.Match(fileName);
                if (!match.Success)
                    continue;

                var icao = match.Groups[1].Value.ToUpperInvariant();
                var dir = Path.GetDirectoryName(ini)!;

                // Begleit-Skripte (auch "_handler.py"): alle py mit gleichem ICAO-Präfix im selben
                // Ordner; sonst im Elternordner suchen ("Choose ONE"-Muster: inis im Unterordner,
                // geteilte py eine Ebene höher).
                List<string> IcaoPys(string searchDir) => Directory.EnumerateFiles(searchDir, "*.py")
                    .Where(p => IcaoPrefix.Match(Path.GetFileName(p)) is { Success: true } m
                                && m.Groups[1].Value.Equals(icao, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var pyFiles = IcaoPys(dir);
                if (pyFiles.Count == 0)
                {
                    var parent = Path.GetDirectoryName(dir);
                    if (parent is not null && parent.Length >= Path.GetFullPath(root).Length)
                        pyFiles = IcaoPys(parent);
                }

                var relDir = Path.GetRelativePath(root, dir);
                var label = relDir == "." ? fileName : $"{relDir}{Path.DirectorySeparatorChar}{fileName}";
                var flavor = DetectVdgsFlavor(label);
                // Hinweis steckt manchmal nur im Archivnamen (z.B. "LPPT_GSX_VDGS.rar" mit schlichter LPPT.ini).
                if (flavor == "Standard" && fallbackContext is not null)
                    flavor = DetectVdgsFlavor(fallbackContext);
                var files = new List<string> { ini };
                files.AddRange(pyFiles);
                variants.Add((icao, new GsxProfileVariant(label, flavor, files)));
            }
        }
        catch { }

        return variants
            .GroupBy(v => v.Icao, StringComparer.OrdinalIgnoreCase)
            .Select(g => new GsxProfileCandidate(g.Key, g.Select(v => v.Variant).ToList()))
            .OrderBy(c => c.Icao, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string AirplanesPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "virtuali", "Airplanes");

    /// <summary>
    /// Sucht GSX-Aircraft-Configs: ein „Airplanes“-Ordner (beliebige Tiefe ≤ 5) mit
    /// Flugzeug-Unterordnern voller .cfg-Dateien (Muster LH_VIRTUAL_GSX_CONFIGS/Virtuali/Airplanes/FNX_320_CFM/GSX-D-AIUB.cfg).
    /// </summary>
    public static List<GsxAircraftConfig> FindAircraftConfigs(string root)
    {
        var result = new List<GsxAircraftConfig>();
        try
        {
            void Walk(string dir, int depth)
            {
                if (string.Equals(Path.GetFileName(dir), "Airplanes", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var aircraftDir in Directory.EnumerateDirectories(dir))
                    {
                        var count = Directory.EnumerateFiles(aircraftDir, "*.cfg").Count();
                        if (count > 0)
                            result.Add(new GsxAircraftConfig(Path.GetFileName(aircraftDir), aircraftDir, count));
                    }
                    return;
                }
                if (depth >= 5)
                    return;
                foreach (var sub in Directory.EnumerateDirectories(dir))
                    Walk(sub, depth + 1);
            }
            Walk(root, 0);
        }
        catch { }
        return result;
    }

    /// <summary>Kopiert die cfg-Dateien einer Aircraft-Config nach %APPDATA%\virtuali\Airplanes\&lt;Ordner&gt;.
    /// Überschriebene Dateien wandern vorher ins Backup.</summary>
    public static (bool Ok, string Message) InstallAircraftConfig(GsxAircraftConfig config, string? targetBaseOverride = null)
    {
        try
        {
            var targetDir = Path.Combine(targetBaseOverride ?? AirplanesPath, config.AircraftFolder);
            Directory.CreateDirectory(targetDir);

            string? backupDir = null;
            var replaced = 0;
            foreach (var file in Directory.EnumerateFiles(config.SourceDir, "*.cfg"))
            {
                var destination = Path.Combine(targetDir, Path.GetFileName(file));
                if (File.Exists(destination))
                {
                    backupDir ??= Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "Msfs2024AiTuner", "gsx-backup", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"), config.AircraftFolder);
                    Directory.CreateDirectory(backupDir);
                    File.Copy(destination, Path.Combine(backupDir, Path.GetFileName(destination)), overwrite: true);
                    replaced++;
                }
                File.Copy(file, destination, overwrite: true);
            }

            var replacedNote = replaced > 0 ? $" ({replaced} ersetzt, alte Versionen im Backup)" : "";
            return (true, $"Aircraft-Configs „{config.AircraftFolder}“: {config.FileCount} Datei(en) installiert{replacedNote}.");
        }
        catch (Exception ex)
        {
            return (false, $"Aircraft-Configs „{config.AircraftFolder}“: {ex.Message}");
        }
    }

    /// <summary>Empfohlene Variante: passend zum installierten VDGS-Add-on, sonst GSX-eigenes VDGS/Standard.</summary>
    public static GsxProfileVariant SuggestVariant(GsxProfileCandidate candidate, bool hasNool, bool hasAerosoftVdgs)
    {
        int Score(GsxProfileVariant v) => v.Vdgs switch
        {
            "nool VDGS" => hasNool ? 4 : 0,
            "Aerosoft VDGS" => hasAerosoftVdgs ? 4 : 0,
            "GSX VDGS" => 2,
            _ => 1,
        };
        return candidate.Variants.OrderByDescending(Score).First();
    }

    /// <summary>
    /// Installiert eine Variante in den GSX-Profilordner. Vorhandene Dateien desselben ICAO
    /// wandern vorher in ein Backup (damit GSX nie zwei Profile für einen Airport sieht).
    /// </summary>
    public static (bool Ok, string Message) InstallVariant(string icao, GsxProfileVariant variant,
        bool replaceExisting, string? targetDirOverride = null)
    {
        try
        {
            var target = targetDirOverride ?? ProfilesPath;
            if (!Directory.Exists(target))
                return (false, $"GSX-Profilordner nicht gefunden: {target}");

            var existing = Directory.EnumerateFiles(target)
                .Where(f => IcaoPrefix.Match(Path.GetFileName(f)) is { Success: true } m
                            && m.Groups[1].Value.Equals(icao, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (existing.Count > 0 && !replaceExisting)
                return (false, "EXISTS");

            if (existing.Count > 0)
            {
                var backupDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Msfs2024AiTuner", "gsx-backup", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
                Directory.CreateDirectory(backupDir);
                foreach (var file in existing)
                    File.Move(file, Path.Combine(backupDir, Path.GetFileName(file)), overwrite: true);
            }

            foreach (var file in variant.Files)
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);

            var backupNote = existing.Count > 0 ? $" ({existing.Count} alte Datei(en) ins Backup verschoben)" : "";
            return (true, $"GSX-Profil {icao} installiert: {variant.Vdgs}{backupNote}.");
        }
        catch (Exception ex)
        {
            return (false, $"GSX-Profil {icao}: {ex.Message}");
        }
    }
}
