using System.Text;
using System.Text.RegularExpressions;

namespace AiTuner.Core.Msfs;

/// <summary>
/// MSFS 2024 stores the rolling-cache folder in the binary "localprofile" file (LocalState),
/// not in UserCfg.opt. This extracts path candidates from that file (ASCII and UTF-16) and
/// resolves the actual ROLLINGCACHE.CCC file.
/// </summary>
public static class RollingCacheLocator
{
    private const string CacheFileName = "ROLLINGCACHE.CCC";

    public static string? FindRollingCacheFile(MsfsInstallation installation)
    {
        foreach (var profileFile in CandidateProfileFiles(installation))
        {
            foreach (var candidate in ExtractPathCandidates(profileFile))
            {
                try
                {
                    if (candidate.EndsWith(".ccc", StringComparison.OrdinalIgnoreCase) && File.Exists(candidate))
                        return candidate;
                    var inFolder = Path.Combine(candidate, CacheFileName);
                    if (File.Exists(inFolder))
                        return inFolder;
                }
                catch { }
            }
        }

        try
        {
            var configDir = Path.GetDirectoryName(installation.UserCfgPath);
            if (configDir is not null)
            {
                var defaultPath = Path.Combine(configDir, CacheFileName);
                if (File.Exists(defaultPath))
                    return defaultPath;
            }
        }
        catch { }

        return null;
    }

    private static IEnumerable<string> CandidateProfileFiles(MsfsInstallation installation)
    {
        var configDir = Path.GetDirectoryName(installation.UserCfgPath);
        if (configDir is null)
            yield break;

        // Store: ...\Packages\Microsoft.Limitless_...\LocalCache\UserCfg.opt → package root\LocalState\localprofile
        var packageRoot = Path.GetDirectoryName(configDir);
        var candidates = new[]
        {
            packageRoot is null ? null : Path.Combine(packageRoot, "LocalState", "localprofile"),
            Path.Combine(configDir, "localprofile"),
            Path.Combine(configDir, "LocalState", "localprofile"),
        };

        foreach (var candidate in candidates)
        {
            if (candidate is not null && File.Exists(candidate))
                yield return candidate;
        }
    }

    private static IEnumerable<string> ExtractPathCandidates(string profileFile)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(profileFile);
        }
        catch
        {
            yield break;
        }

        // Drive-letter paths; control chars and '?' (the ASCII rendering of non-ASCII bytes) end a match.
        var pathPattern = new Regex(@"[A-Za-z]:\\[^\x00-\x1f""<>|?*]{1,220}");

        foreach (var text in new[] { Encoding.ASCII.GetString(bytes), Encoding.Unicode.GetString(bytes) })
        {
            foreach (Match match in pathPattern.Matches(text))
                yield return match.Value.Trim();
        }
    }
}
