using System.Diagnostics;
using System.Text.RegularExpressions;
using AiTuner.Core.Msfs;
using Microsoft.Win32;

namespace AiTuner.Core.Nvidia;

public sealed record DlssRuntimeInfo(string Source, string FileName, string Version, string Path);

/// <summary>
/// Best-effort search for DLSS runtime DLLs (nvngx_dlss*.dll): in the MSFS 2024 game folders
/// (Steam libraries; Store packages are ACL-protected) and in the driver's NGX override store.
/// </summary>
public static class DlssScanner
{
    private static readonly string[] DlssFileNames =
    [
        "nvngx_dlss.dll",   // Super Resolution
        "nvngx_dlssg.dll",  // Frame Generation
        "nvngx_dlssd.dll",  // Ray Reconstruction
    ];

    public static IReadOnlyList<DlssRuntimeInfo> Scan(IEnumerable<MsfsInstallation> installations)
    {
        var results = new List<DlssRuntimeInfo>();

        foreach (var gameDir in FindGameDirectories(installations))
            ProbeDirectory(results, gameDir.Path, gameDir.Source, recursive: false);

        var ngxModels = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "NVIDIA", "NGX", "models");
        ProbeDirectory(results, ngxModels, "Treiber-Override (NGX)", recursive: true);

        return results;
    }

    private static IEnumerable<(string Path, string Source)> FindGameDirectories(IEnumerable<MsfsInstallation> installations)
    {
        foreach (var installation in installations)
        {
            if (installation.Edition == MsfsEdition.Steam && installation.UserCfgExists)
            {
                foreach (var library in FindSteamLibraries())
                {
                    var gameDir = System.IO.Path.Combine(library, "steamapps", "common", "Microsoft Flight Simulator 2024");
                    if (Directory.Exists(gameDir))
                        yield return (gameDir, "Steam-Spielverzeichnis");
                }
            }
        }

        // Store packages live under WindowsApps; listing usually fails without elevated ACLs — try anyway.
        string[] packageDirs;
        try
        {
            packageDirs = Directory.GetDirectories(@"C:\Program Files\WindowsApps", "Microsoft.Limitless_*");
        }
        catch
        {
            yield break;
        }
        foreach (var dir in packageDirs)
            yield return (dir, "Store-Spielverzeichnis");
    }

    private static IEnumerable<string> FindSteamLibraries()
    {
        string? steamPath = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            steamPath = key?.GetValue("SteamPath") as string;
        }
        catch { }

        if (string.IsNullOrWhiteSpace(steamPath))
            yield break;

        steamPath = steamPath.Replace('/', '\\');
        yield return steamPath;

        var vdf = System.IO.Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        string content;
        try
        {
            if (!File.Exists(vdf))
                yield break;
            content = File.ReadAllText(vdf);
        }
        catch
        {
            yield break;
        }

        foreach (Match match in Regex.Matches(content, "\"path\"\\s+\"([^\"]+)\""))
        {
            var path = match.Groups[1].Value.Replace(@"\\", @"\");
            if (!string.Equals(path, steamPath, StringComparison.OrdinalIgnoreCase))
                yield return path;
        }
    }

    private static void ProbeDirectory(List<DlssRuntimeInfo> results, string directory, string source, bool recursive)
    {
        try
        {
            if (!Directory.Exists(directory))
                return;

            foreach (var fileName in DlssFileNames)
            {
                var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                foreach (var file in Directory.EnumerateFiles(directory, fileName, option))
                {
                    var version = "?";
                    try { version = FileVersionInfo.GetVersionInfo(file).FileVersion ?? "?"; } catch { }
                    if (!results.Any(r => r.Source == source && r.FileName == fileName && r.Version == version))
                        results.Add(new DlssRuntimeInfo(source, fileName, version, file));
                }
            }
        }
        catch
        {
            // Zugriff verweigert (z.B. WindowsApps) — still bleiben, Anzeige meldet das generisch.
        }
    }
}
