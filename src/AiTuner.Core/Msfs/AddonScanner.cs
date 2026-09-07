using System.Text.Json;

namespace AiTuner.Core.Msfs;

public sealed record MsfsAddon(
    string Edition,
    string PackageName,
    string Title,
    string ContentType,
    string Version,
    string Creator,
    bool HasWasm,
    string Path,
    bool DeactivatedInContent = false) // laut Content.xml (UserDisabled/SystemDisabled) — Sim lädt es nicht
{
    /// <summary>Heuristisch erkannte Airport-ICAOs dieses Pakets.</summary>
    public IReadOnlyList<string> Icaos { get; init; } = [];
}

public sealed class AddonScanResult
{
    public IReadOnlyList<MsfsAddon> Addons { get; init; } = [];
    public IReadOnlyList<string> Issues { get; init; } = [];
    public IReadOnlyList<IcaoConflict> IcaoConflicts { get; init; } = [];
    public int AirportCount { get; init; }
}

/// <summary>
/// Inventory of Community add-ons per installation, based on InstalledPackagesPath from UserCfg.opt.
/// Stage 0 does a first cheap conflict pass; deep overlap analysis (ICAO duplicates etc.) is a later stage.
/// </summary>
public static class AddonScanner
{
    public static AddonScanResult Scan(IEnumerable<MsfsInstallation> installations)
    {
        var addons = new List<MsfsAddon>();
        var issues = new List<string>();
        var allOfficialNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var installation in installations)
        {
            var packagesPath = GetInstalledPackagesPath(installation);
            if (packagesPath is null)
                continue;

            // Seit SU4 lädt MSFS 2024 zwei Community-Wurzeln: Community (geteilt mit 2020)
            // und Community2024 (nur native 2024-Pakete).
            var communityDirs = new[] { "Community", "Community2024" }
                .Select(name => System.IO.Path.Combine(packagesPath, name))
                .Where(Directory.Exists)
                .ToList();
            if (communityDirs.Count == 0)
                continue;

            var officialNames = ReadOfficialPackageNames(packagesPath);
            foreach (var name in officialNames)
                allOfficialNames.Add(name);

            // Content.xml: (a) deaktivierte Community-Pakete werden NICHT geladen — kein Konflikt;
            // (b) die aktiven Official-Einträge (fs24-/fs20-) sind die vollständigste Quelle für
            // offizielle Airports — inklusive gestreamter Pakete, die nicht auf der Platte liegen.
            var contentPackages = ContentXmlService.Load(installation);
            var contentStates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var package in contentPackages.Where(p => p.IsCommunity))
                contentStates[package.PackageName] = package.ActiveState;
            foreach (var package in contentPackages.Where(p => !p.IsCommunity && p.IsActive))
            {
                officialNames.Add(package.PackageName);
                allOfficialNames.Add(package.PackageName);
            }

            foreach (var communityDir in communityDirs)
            foreach (var dir in Directory.EnumerateDirectories(communityDir))
            {
                var addon = ReadAddon(installation.EditionName, dir);
                var folderName = System.IO.Path.GetFileName(dir);
                if (contentStates.TryGetValue(folderName, out var state)
                    && !state.Equals("Activated", StringComparison.OrdinalIgnoreCase))
                    addon = addon with { DeactivatedInContent = true };
                addons.Add(addon);

                if (!addon.DeactivatedInContent && officialNames.Contains(addon.PackageName))
                    issues.Add($"„{addon.PackageName}“ ({System.IO.Path.GetFileName(communityDir)}, {installation.EditionName}) überdeckt ein offizielles Paket gleichen Namens.");
            }
        }

        var officialAirportIcaos = IcaoAnalyzer.ExtractOfficialAirportIcaos(allOfficialNames);
        // Nur Pakete, die der Sim wirklich lädt, können kollidieren.
        var loadedAddons = addons.Where(a => !a.DeactivatedInContent).ToList();
        var icaoConflicts = IcaoAnalyzer.FindConflicts(loadedAddons, officialAirportIcaos);
        var airportCount = loadedAddons.SelectMany(a => a.Icaos).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        foreach (var group in loadedAddons
            .Where(a => !string.IsNullOrWhiteSpace(a.Title))
            .GroupBy(a => a.Title, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1))
        {
            issues.Add($"Mehrere Add-ons mit identischem Titel „{group.Key}“ ({group.Count()}×) — mögliche Duplikate oder Überlappung.");
        }

        return new AddonScanResult
        {
            Addons = addons,
            Issues = issues,
            IcaoConflicts = icaoConflicts,
            AirportCount = airportCount,
        };
    }

    public static string? GetInstalledPackagesPath(MsfsInstallation installation)
    {
        var cfg = installation.LoadUserCfg();
        var entry = cfg?.Flatten().FirstOrDefault(e =>
            e.Key.Equals("InstalledPackagesPath", StringComparison.OrdinalIgnoreCase));
        var path = entry?.Value.Trim().Trim('"');
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    private static HashSet<string> ReadOfficialPackageNames(string packagesPath)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // MSFS 2020: "Official"; MSFS 2024 seit SU4: "Official2020" + "Official2024".
            foreach (var folderName in new[] { "Official", "Official2020", "Official2024" })
            {
                var officialDir = System.IO.Path.Combine(packagesPath, folderName);
                if (!Directory.Exists(officialDir))
                    continue;

                // Official*\<OneStore|Steam>\<package folders>
                foreach (var storeDir in Directory.EnumerateDirectories(officialDir))
                    foreach (var package in Directory.EnumerateDirectories(storeDir))
                        names.Add(System.IO.Path.GetFileName(package));
            }
        }
        catch { }
        return names;
    }

    private static MsfsAddon ReadAddon(string edition, string dir)
    {
        var packageName = System.IO.Path.GetFileName(dir);
        var title = packageName;
        var contentType = "Unbekannt";
        var version = "";
        var creator = "";

        try
        {
            var manifestPath = System.IO.Path.Combine(dir, "manifest.json");
            if (File.Exists(manifestPath))
            {
                using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
                var root = manifest.RootElement;
                title = GetString(root, "title") ?? title;
                contentType = GetString(root, "content_type") ?? contentType;
                version = GetString(root, "package_version") ?? "";
                creator = GetString(root, "creator") ?? "";
            }
        }
        catch { }

        var hasWasm = false;
        var bglFileNames = new List<string>();
        try
        {
            // layout.json lists every file of the package — cheap way to detect WASM modules and BGL names.
            var layoutPath = System.IO.Path.Combine(dir, "layout.json");
            if (File.Exists(layoutPath))
            {
                var layout = File.ReadAllText(layoutPath);
                hasWasm = layout.Contains(".wasm", StringComparison.OrdinalIgnoreCase);
                foreach (System.Text.RegularExpressions.Match match in
                         System.Text.RegularExpressions.Regex.Matches(layout, @"([A-Za-z0-9_\-\.]+)\.[bB][gG][lL]"))
                    bglFileNames.Add(match.Groups[1].Value);
            }
        }
        catch { }

        // Exakte ICAOs aus den BGL-Binärdaten (Airport-Records) — schlägt die Namens-Heuristik.
        // Nur für Szenerie-Pakete sinnvoll; Größen-Sanity über die Sektionstabelle im Parser.
        var exactIcaos = new List<string>();
        if (contentType.Equals("SCENERY", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                foreach (var bgl in Directory.EnumerateFiles(dir, "*.bgl", SearchOption.AllDirectories).Take(200))
                    foreach (var icao in BglParser.ReadAirportIcaos(bgl))
                        if (!exactIcaos.Contains(icao))
                            exactIcaos.Add(icao);
            }
            catch { }
        }

        return new MsfsAddon(edition, packageName, title, contentType, version, creator, hasWasm, dir)
        {
            Icaos = exactIcaos.Count > 0
                ? exactIcaos
                : IcaoAnalyzer.ExtractIcaos(title, packageName, bglFileNames),
        };
    }

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
