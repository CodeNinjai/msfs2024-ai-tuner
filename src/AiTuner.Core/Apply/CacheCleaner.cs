namespace AiTuner.Core.Apply;

/// <summary>Deletes cache contents (shader caches, rolling cache). Refuses while the sim runs.</summary>
public static class CacheCleaner
{
    /// <summary>Empties a cache directory (contents only, directory stays). Locked files are skipped.</summary>
    public static (bool Ok, string Message) ClearDirectory(string path)
    {
        if (MsfsApplier.IsSimRunning())
            return (false, "MSFS läuft — Caches bitte erst nach dem Beenden des Sims leeren.");
        if (!Directory.Exists(path))
            return (false, "Ordner existiert nicht.");

        long freedBytes = 0;
        var skipped = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var size = new FileInfo(file).Length;
                    File.Delete(file);
                    freedBytes += size;
                }
                catch
                {
                    skipped++;
                }
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }

        var freedMb = Math.Round(freedBytes / 1048576.0);
        return (true, skipped == 0
            ? $"{freedMb:#,0} MB freigegeben."
            : $"{freedMb:#,0} MB freigegeben, {skipped} gesperrte Datei(en) übersprungen.");
    }

    /// <summary>Deletes the rolling-cache file. The sim recreates it (empty) on next start.</summary>
    public static (bool Ok, string Message) DeleteFile(string path)
    {
        if (MsfsApplier.IsSimRunning())
            return (false, "MSFS läuft — der Rolling Cache ist gesperrt. Bitte zuerst den Sim schließen.");
        if (!File.Exists(path))
            return (false, "Datei existiert nicht.");

        try
        {
            var sizeMb = Math.Round(new FileInfo(path).Length / 1048576.0);
            File.Delete(path);
            return (true, $"{sizeMb:#,0} MB gelöscht — der Sim legt den Cache beim nächsten Start neu an.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
