using System.Text;
using System.Text.Json;

namespace AiTuner.Core.Msfs;

/// <summary>Ergebnis eines layout.json-Abgleichs für ein Paket.</summary>
public sealed record LayoutCheckResult(
    string PackageDir,
    bool HasLayout,
    int ListedCount,
    List<string> MissingFiles,     // gelistet, fehlt wirklich (keine .off-Variante) → Paket beschädigt
    List<string> SizeMismatches,   // gelistet, aber Größe weicht ab → Sim ignoriert/Fehler
    List<string> UnlistedFiles,    // sim-relevante Datei nicht gelistet → Sim ignoriert sie (Livery-Klassiker)
    int DisabledViaOffCount,       // per Konfigurator deaktiviert (.off-Muster) — Absicht, kein Fehler
    int HarmlessUnlistedCount)     // Doku/PDF/GSX-Beilagen — für den Sim irrelevant
{
    public bool IsClean => HasLayout && MissingFiles.Count == 0 && SizeMismatches.Count == 0 && UnlistedFiles.Count == 0;
    public int IssueCount => MissingFiles.Count + SizeMismatches.Count + UnlistedFiles.Count;

    public string Summary
    {
        get
        {
            if (!HasLayout)
                return "keine layout.json";
            var parts = new List<string>();
            if (UnlistedFiles.Count > 0) parts.Add($"{UnlistedFiles.Count} sim-relevante Datei(en) nicht gelistet — werden IGNORIERT");
            if (SizeMismatches.Count > 0) parts.Add($"{SizeMismatches.Count} mit falscher Größe");
            if (MissingFiles.Count > 0) parts.Add($"{MissingFiles.Count} fehlend");
            if (parts.Count == 0)
                return DisabledViaOffCount > 0 ? $"konsistent ({DisabledViaOffCount} Option(en) per .off deaktiviert)" : "konsistent";
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// Prüft und repariert die layout.json von MSFS-Paketen. Der Sim lädt Dateien strikt nach
/// dieser Liste — nicht gelistete oder größenabweichende Dateien werden ignoriert
/// (der Klassiker: manuell installierte Livery taucht nicht auf).
/// </summary>
public static class LayoutService
{
    private static readonly HashSet<string> ExcludedNames = new(StringComparer.OrdinalIgnoreCase)
        { "layout.json", "manifest.json", "business.json", "thumbs.db", "desktop.ini" };

    // Nur diese Dateitypen sind für den Sim relevant — eine ungelistete Doku-PDF ist kein Problem.
    private static readonly HashSet<string> SimRelevantExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bgl", ".dds", ".ktx2", ".cfg", ".flt", ".air", ".wasm", ".spb",
        ".gltf", ".bin", ".xml", ".json", ".htm", ".html", ".js", ".wav", ".pc2", ".loc",
    };

    private static bool IsExcluded(string relativePath)
    {
        var name = Path.GetFileName(relativePath);
        return ExcludedNames.Contains(name)
               || name.StartsWith('.')
               || name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".off", StringComparison.OrdinalIgnoreCase); // Konfigurator-Muster
    }

    /// <summary>Aerosoft-Konfigurator-Muster: „X“ fehlt, aber „X.off“ (Datei oder Vorfahr-Ordner) existiert.</summary>
    private static bool IsDisabledViaOff(string packageDir, string relativePath)
    {
        var full = Path.Combine(packageDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(full + ".off"))
            return true;
        var directory = Path.GetDirectoryName(full);
        while (directory is not null && directory.Length > packageDir.Length)
        {
            if (Directory.Exists(directory + ".off"))
                return true;
            directory = Path.GetDirectoryName(directory);
        }
        return false;
    }

    public static LayoutCheckResult Check(string packageDir)
    {
        var layoutPath = Path.Combine(packageDir, "layout.json");
        var missing = new List<string>();
        var mismatched = new List<string>();
        var unlisted = new List<string>();
        var disabledViaOff = 0;
        var harmlessUnlisted = 0;

        if (!File.Exists(layoutPath))
            return new LayoutCheckResult(packageDir, false, 0, missing, mismatched, unlisted, 0, 0);

        var listed = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(layoutPath));
            if (document.RootElement.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                foreach (var entry in content.EnumerateArray())
                {
                    var path = entry.TryGetProperty("path", out var p) ? p.GetString() : null;
                    if (string.IsNullOrEmpty(path))
                        continue;
                    var size = entry.TryGetProperty("size", out var s) && s.TryGetInt64(out var sizeValue) ? sizeValue : -1;
                    listed[path.Replace('\\', '/')] = size;
                }
        }
        catch
        {
            return new LayoutCheckResult(packageDir, false, 0, missing, mismatched, unlisted, 0, 0);
        }

        foreach (var (relativePath, expectedSize) in listed)
        {
            // Selbstbezügliche Einträge (manche Pakete listen layout/manifest selbst) überspringen.
            if (ExcludedNames.Contains(Path.GetFileName(relativePath)))
                continue;
            var fullPath = Path.Combine(packageDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                if (IsDisabledViaOff(packageDir, relativePath))
                    disabledViaOff++;
                else
                    missing.Add(relativePath);
            }
            else if (expectedSize >= 0 && new FileInfo(fullPath).Length != expectedSize)
                mismatched.Add(relativePath);
        }

        foreach (var file in Directory.EnumerateFiles(packageDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(packageDir, file).Replace('\\', '/');
            if (IsExcluded(relativePath))
                continue;
            if (listed.ContainsKey(relativePath))
                continue;
            if (SimRelevantExtensions.Contains(Path.GetExtension(relativePath)))
                unlisted.Add(relativePath);
            else
                harmlessUnlisted++;
        }

        return new LayoutCheckResult(packageDir, true, listed.Count, missing, mismatched, unlisted, disabledViaOff, harmlessUnlisted);
    }

    /// <summary>
    /// Erzeugt die layout.json aus dem echten Ordnerinhalt neu (Backup der alten Datei).
    /// Einträge für per Konfigurator deaktivierte Dateien (.off-Muster) werden aus der alten
    /// layout.json ÜBERNOMMEN, damit das Reaktivieren im Hersteller-Tool weiter funktioniert.
    /// </summary>
    public static (bool Ok, string Message) Regenerate(string packageDir)
    {
        try
        {
            var layoutPath = Path.Combine(packageDir, "layout.json");

            // Alte Einträge einlesen, um .off-deaktivierte Optionen zu erhalten.
            var preserved = new List<(string Path, long Size, long Date)>();
            if (File.Exists(layoutPath))
            {
                try
                {
                    using var oldDocument = JsonDocument.Parse(File.ReadAllText(layoutPath));
                    if (oldDocument.RootElement.TryGetProperty("content", out var oldContent) && oldContent.ValueKind == JsonValueKind.Array)
                        foreach (var entry in oldContent.EnumerateArray())
                        {
                            var path = entry.TryGetProperty("path", out var p) ? p.GetString() : null;
                            if (string.IsNullOrEmpty(path))
                                continue;
                            var normalized = path.Replace('\\', '/');
                            var fullPath = Path.Combine(packageDir, normalized.Replace('/', Path.DirectorySeparatorChar));
                            if (!File.Exists(fullPath) && IsDisabledViaOff(packageDir, normalized))
                                preserved.Add((normalized,
                                    entry.TryGetProperty("size", out var s) && s.TryGetInt64(out var sizeValue) ? sizeValue : 0,
                                    entry.TryGetProperty("date", out var d) && d.TryGetInt64(out var dateValue) ? dateValue : 0));
                        }
                }
                catch { }
            }

            if (File.Exists(layoutPath))
            {
                var backupDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Msfs2024AiTuner", "backups", "layouts");
                Directory.CreateDirectory(backupDir);
                var backupName = $"{Path.GetFileName(packageDir)}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_layout.json";
                File.WriteAllBytes(Path.Combine(backupDir, backupName), File.ReadAllBytes(layoutPath));
            }

            var entries = new List<(string Path, long Size, long Date)>();
            foreach (var file in Directory.EnumerateFiles(packageDir, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(packageDir, file).Replace('\\', '/');
                if (IsExcluded(relativePath))
                    continue;
                var info = new FileInfo(file);
                entries.Add((relativePath, info.Length, info.LastWriteTimeUtc.ToFileTimeUtc()));
            }
            entries.AddRange(preserved);
            entries.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));

            var builder = new StringBuilder();
            builder.Append("{\n  \"content\": [\n");
            for (var i = 0; i < entries.Count; i++)
            {
                if (i > 0)
                    builder.Append(",\n");
                builder.Append("    {\n");
                builder.Append($"      \"path\": {JsonSerializer.Serialize(entries[i].Path)},\n");
                builder.Append($"      \"size\": {entries[i].Size},\n");
                builder.Append($"      \"date\": {entries[i].Date}\n");
                builder.Append("    }");
            }
            builder.Append("\n  ]\n}");

            // WriteAllBytes-Weg (EFS-sicher, kein BOM — der Sim mag layout.json ohne BOM).
            File.WriteAllBytes(layoutPath, Encoding.UTF8.GetBytes(builder.ToString()));
            var preservedNote = preserved.Count > 0 ? $" ({preserved.Count} deaktivierte Option(en) beibehalten)" : "";
            return (true, $"layout.json neu erzeugt — {entries.Count} Datei(en) gelistet{preservedNote}, alte Version im Backup.");
        }
        catch (Exception ex)
        {
            return (false, "layout.json konnte nicht erzeugt werden: " + ex.Message);
        }
    }
}
