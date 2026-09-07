using System.IO.Compression;
using AiTuner.Core.Apply;

namespace AiTuner.Core.Libraries;

/// <summary>Ein installierbares Add-on-Paket (Ordner mit manifest.json) aus einer Quelle.</summary>
public sealed record InstallCandidate(string SourcePath, string FolderName, string Title, string Version);

/// <summary>Ergebnis der Quellanalyse. TempDir (bei Archiven) nach der Installation aufräumen.</summary>
public sealed record InstallPreparation(
    List<InstallCandidate> Candidates,
    string? TempDir,          // entpacktes Archiv — Kandidaten dürfen VERSCHOBEN werden
    string SourceLabel,
    string? Error = null)
{
    /// <summary>true = Quelle ist ein Nutzer-Ordner, Inhalt muss KOPIERT werden.</summary>
    public bool SourceIsUserFolder => TempDir is null;

    /// <summary>Gefundene GSX-Profile (Varianten je Airport) — unabhängig von MSFS-Paketen.</summary>
    public List<GsxProfileCandidate> GsxProfiles { get; init; } = new();

    /// <summary>Gefundene GSX-Aircraft-Configs (Airplanes\*.cfg — z.B. Fenix-Flottenprofile).</summary>
    public List<GsxAircraftConfig> GsxAircraftConfigs { get; init; } = new();
}

/// <summary>
/// Installiert Add-ons aus Ordnern oder Archiven (ZIP nativ; 7z/RAR über eine
/// vorhandene 7-Zip-Installation) in den Community-Ordner oder eine Bibliothek.
/// </summary>
public static class AddonInstaller
{
    private static readonly string[] SevenZipCandidates =
    {
        @"C:\Program Files\7-Zip\7z.exe",
        @"C:\Program Files (x86)\7-Zip\7z.exe",
    };

    public static bool IsArchive(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".zip" or ".7z" or ".rar";
    }

    /// <summary>Analysiert eine Quelle (Ordner oder Archiv) und findet alle Paket-Wurzeln.</summary>
    public static InstallPreparation Prepare(string sourcePath)
    {
        var label = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourcePath));
        try
        {
            if (Directory.Exists(sourcePath))
            {
                var candidates = FindPackageRoots(sourcePath, label);
                var gsx = GsxProfileService.FindProfiles(sourcePath, candidates.Select(c => c.SourcePath).ToList(), label);
                var aircraft = GsxProfileService.FindAircraftConfigs(sourcePath);
                return new InstallPreparation(candidates, TempDir: null, label,
                    candidates.Count == 0 && gsx.Count == 0 && aircraft.Count == 0
                        ? $"„{label}“: weder manifest.json noch GSX-Inhalte gefunden — kein installierbarer Inhalt."
                        : null)
                { GsxProfiles = gsx, GsxAircraftConfigs = aircraft };
            }

            if (!File.Exists(sourcePath))
                return new InstallPreparation(new(), null, label, $"„{label}“: Quelle nicht gefunden.");

            if (!IsArchive(sourcePath))
                return new InstallPreparation(new(), null, label,
                    $"„{label}“: nicht unterstützt — bitte einen Ordner oder ein Archiv (ZIP/7z/RAR) wählen.");

            var tempDir = Path.Combine(Path.GetTempPath(), "AiTunerInstall_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(tempDir);

            var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (ext == ".zip")
            {
                ZipFile.ExtractToDirectory(sourcePath, tempDir, overwriteFiles: true);
            }
            else
            {
                var sevenZip = SevenZipCandidates.FirstOrDefault(File.Exists);
                if (sevenZip is null)
                {
                    TryDeleteTemp(tempDir);
                    return new InstallPreparation(new(), null, label,
                        $"„{label}“: Für {ext.ToUpperInvariant().TrimStart('.')}-Archive wird 7-Zip benötigt (nicht gefunden). Bitte 7-Zip installieren oder das Archiv vorher entpacken.");
                }
                ProcessRunner.Run(sevenZip, $"x -y -o\"{tempDir}\" \"{sourcePath}\"");
            }

            var archiveName = SanitizeFolderName(Path.GetFileNameWithoutExtension(sourcePath));
            var found = FindPackageRoots(tempDir, archiveName);
            var gsxProfiles = GsxProfileService.FindProfiles(tempDir, found.Select(c => c.SourcePath).ToList(), label);
            var aircraftConfigs = GsxProfileService.FindAircraftConfigs(tempDir);
            if (found.Count == 0 && gsxProfiles.Count == 0 && aircraftConfigs.Count == 0)
            {
                TryDeleteTemp(tempDir);
                return new InstallPreparation(new(), null, label,
                    $"„{label}“: im Archiv weder manifest.json noch GSX-Inhalte gefunden — kein installierbarer Inhalt.");
            }
            return new InstallPreparation(found, tempDir, label) { GsxProfiles = gsxProfiles, GsxAircraftConfigs = aircraftConfigs };
        }
        catch (Exception ex)
        {
            return new InstallPreparation(new(), null, label, $"„{label}“: {ex.Message}");
        }
    }

    /// <summary>
    /// Installiert einen Kandidaten. targetLibrary = null → Community-Ordner (fest, aktiv).
    /// Bei Bibliothek: activate legt zusätzlich eine Junction im Community-Ordner an.
    /// </summary>
    public static (bool Ok, string Message) Install(
        InstallCandidate candidate, string communityPath, string? targetLibrary,
        bool activate, bool copyInsteadOfMove, bool overwrite)
    {
        try
        {
            var targetBase = targetLibrary ?? communityPath;
            Directory.CreateDirectory(targetBase);
            var destination = Path.Combine(targetBase, candidate.FolderName);
            var linkPath = Path.Combine(communityPath, candidate.FolderName);

            if (Directory.Exists(destination) || File.Exists(destination))
            {
                if (!overwrite)
                    return (false, "EXISTS");
                var destInfo = new DirectoryInfo(destination);
                if ((destInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                    Directory.Delete(destination, recursive: false); // Link: nur den Link entfernen
                else
                    Directory.Delete(destination, recursive: true);
            }

            if (copyInsteadOfMove)
                LibraryService.CopyDirectory(candidate.SourcePath, destination);
            else
                LibraryService.MoveDirectoryRobust(candidate.SourcePath, destination);

            if (targetLibrary is null)
                return (true, $"„{candidate.Title}“ in den Community-Ordner installiert (aktiv).");

            if (!activate)
                return (true, $"„{candidate.Title}“ in die Bibliothek installiert (inaktiv — Aktivieren jederzeit per Klick).");

            // Vorhandenen Link gleichen Namens ersetzen, echte Ordner nicht anfassen.
            var linkInfo = new DirectoryInfo(linkPath);
            try
            {
                if ((int)linkInfo.Attributes != -1 && (linkInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                    Directory.Delete(linkPath, recursive: false);
            }
            catch { }
            if (Directory.Exists(linkPath))
                return (true, $"„{candidate.Title}“ installiert, aber im Community-Ordner existiert bereits ein echter Ordner „{candidate.FolderName}“ — kein Link angelegt (inaktiv).");

            ProcessRunner.Run("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{destination}\"");
            return Directory.Exists(linkPath)
                ? (true, $"„{candidate.Title}“ in die Bibliothek installiert und aktiviert (Link).")
                : (false, $"„{candidate.Title}“ installiert, aber der Link konnte nicht angelegt werden — Add-on ist inaktiv.");
        }
        catch (Exception ex)
        {
            return (false, $"„{candidate.Title}“: {ex.Message}");
        }
    }

    public static void Cleanup(InstallPreparation prep)
    {
        if (prep.TempDir is not null)
            TryDeleteTemp(prep.TempDir);
    }

    /// <summary>
    /// Findet Paket-Wurzeln (Ordner mit manifest.json), max. 4 Ebenen tief; in gefundene
    /// Pakete wird nicht weiter abgestiegen. Liegt die manifest.json direkt in der Wurzel
    /// (z.B. Archiv ohne Überordner), dient fallbackName als Zielordner-Name.
    /// </summary>
    private static List<InstallCandidate> FindPackageRoots(string root, string fallbackName)
    {
        var result = new List<InstallCandidate>();
        void Walk(string dir, int depth)
        {
            if (File.Exists(Path.Combine(dir, "manifest.json")))
            {
                var folderName = PathsEqualSafe(dir, root)
                    ? SanitizeFolderName(fallbackName)
                    : Path.GetFileName(Path.TrimEndingDirectorySeparator(dir));
                var (title, version) = LibraryService.ReadManifest(dir);
                result.Add(new InstallCandidate(dir, folderName, title, version));
                return;
            }
            if (depth >= 4)
                return;
            foreach (var sub in Directory.EnumerateDirectories(dir))
                Walk(sub, depth + 1);
        }
        Walk(root, 0);
        return result;
    }

    private static bool PathsEqualSafe(string a, string b)
        => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                         Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                         StringComparison.OrdinalIgnoreCase);

    private static string SanitizeFolderName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '-');
        return name.Trim().TrimEnd('.');
    }

    private static void TryDeleteTemp(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }
}
