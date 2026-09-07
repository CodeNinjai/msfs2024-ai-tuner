using AiTuner.Core.Msfs;

namespace AiTuner.Core.Caches;

public sealed record CacheEntry(string Name, string Path, double SizeMb, bool Exists);

/// <summary>
/// Read-only inventory of performance-relevant caches (shader caches, MSFS rolling cache).
/// Clearing them is a stage-2 action once backup/undo exists.
/// </summary>
public static class CacheScanner
{
    public static IReadOnlyList<CacheEntry> Scan(IEnumerable<MsfsInstallation> installations)
    {
        var entries = new List<CacheEntry>();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var localLow = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow");

        AddDirectory(entries, "NVIDIA Shader-Cache (DXCache)", System.IO.Path.Combine(localAppData, "NVIDIA", "DXCache"));
        AddDirectory(entries, "NVIDIA Shader-Cache (GLCache)", System.IO.Path.Combine(localAppData, "NVIDIA", "GLCache"));
        AddDirectory(entries, "NVIDIA Shader-Cache (LocalLow)", System.IO.Path.Combine(localLow, "NVIDIA", "PerDriverVersion", "DXCache"), skipIfMissing: true);
        AddDirectory(entries, "DirectX Shader-Cache (D3DSCache)", System.IO.Path.Combine(localAppData, "D3DSCache"));
        AddDirectory(entries, "AMD Shader-Cache (DxCache)", System.IO.Path.Combine(localAppData, "AMD", "DxCache"), skipIfMissing: true);
        AddDirectory(entries, "AMD Shader-Cache (Dx12Cache)", System.IO.Path.Combine(localAppData, "AMD", "Dx12Cache"), skipIfMissing: true);
        AddDirectory(entries, "AMD Shader-Cache (GLCache)", System.IO.Path.Combine(localAppData, "AMD", "GLCache"), skipIfMissing: true);

        foreach (var installation in installations)
        {
            if (!installation.UserCfgExists)
                continue;

            // MSFS 2024 keeps the rolling-cache folder in the binary "localprofile" file.
            var cacheFile = RollingCacheLocator.FindRollingCacheFile(installation);
            if (cacheFile is not null)
                AddFile(entries, $"MSFS Rolling Cache ({installation.EditionName})", cacheFile);
            else
                entries.Add(new CacheEntry($"MSFS Rolling Cache ({installation.EditionName})", "nicht konfiguriert", 0, false));

            // Szenerie-Index-Cache; Löschen behebt Szenerie-Probleme nach Add-on-Änderungen (Sim baut neu auf).
            var configDir = System.IO.Path.GetDirectoryName(installation.UserCfgPath);
            if (configDir is not null)
                AddDirectory(entries, $"MSFS SceneryIndexes ({installation.EditionName})",
                    System.IO.Path.Combine(configDir, "SceneryIndexes"), skipIfMissing: true);
        }

        return entries;
    }

    private static void AddDirectory(List<CacheEntry> entries, string name, string path, bool skipIfMissing = false)
    {
        double sizeMb = 0;
        var exists = false;
        try
        {
            exists = Directory.Exists(path);
            if (!exists && skipIfMissing)
                return;
            if (exists)
            {
                long bytes = 0;
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { bytes += new FileInfo(file).Length; } catch { }
                }
                sizeMb = Math.Round(bytes / 1048576.0);
            }
        }
        catch { }
        entries.Add(new CacheEntry(name, path, sizeMb, exists));
    }

    private static void AddFile(List<CacheEntry> entries, string name, string path)
    {
        double sizeMb = 0;
        var exists = false;
        try
        {
            exists = File.Exists(path);
            if (exists)
                sizeMb = Math.Round(new FileInfo(path).Length / 1048576.0);
        }
        catch { }
        entries.Add(new CacheEntry(name, path, sizeMb, exists));
    }
}
