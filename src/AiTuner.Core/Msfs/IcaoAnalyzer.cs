using System.Text.RegularExpressions;

namespace AiTuner.Core.Msfs;

public sealed record IcaoConflict(string Icao, IReadOnlyList<string> Packages, bool InvolvesOfficial);

/// <summary>
/// Heuristische ICAO-Erkennung: 4-Buchstaben-Codes aus BGL-Dateinamen (stärkste Quelle),
/// Add-on-Titeln und Paketnamen — gefiltert über gültige ICAO-Regionsbuchstaben und eine
/// Blacklist häufiger Fehltreffer (Hersteller-Kürzel, englische Wörter).
/// </summary>
public static class IcaoAnalyzer
{
    private static readonly Regex TokenPattern = new("(?<![A-Za-z])([A-Za-z]{4})(?![A-Za-z])", RegexOptions.Compiled);

    // Reale ICAO-Regionen: erste Buchstaben ohne I, J, Q, X.
    private static readonly HashSet<char> ValidFirstLetter = new("ABCDEFGHKLMNOPRSTUVWYZ");

    private static readonly HashSet<string> Blacklist = new(StringComparer.OrdinalIgnoreCase)
    {
        // Hersteller/Marken-Kürzel
        "FSDG", "LVFR", "GAYA", "ORBX", "SIMW", "AERO", "DRZE", "FSLT", "MSFS", "ASOB",
        "PMDG", "FVFR", // PMDG-Flieger, FranceVFR-BGL-Präfix
        // englische Wörter aus Livery-/Produkt-Titeln
        "YEAR",
        // häufige Wörter/Abkürzungen in Paket- und Dateinamen
        "BASE", "PACK", "DATA", "MESH", "PROP", "LAND", "CITY", "PORT", "AUTO", "REAL",
        "TRUE", "WASM", "JSON", "AREA", "STAR", "TAXI", "INTL", "WEST", "EAST", "PROJ",
        "TERR", "OBJE", "SCEN", "LIBS", "MATL", "MODL", "TEXT", "SOUN", "FONT", "HTML",
        "AIRP", "PLAN", "PARK", "DOCK", "GATE", "NAVI", "APRO", "GRAS", "ROAD", "WORL",
        "EDIT", "GAME", "MAIN", "DEMO", "TEST", "FULL", "LITE", "PLUS", "GOLD", "BETA",
        // generische Szenerie-Objekt-Kürzel in BGL-Dateinamen (validiert an echten Paketen)
        "BLDG", "CARS", "HVEH", "LVEH", "VDGS", "UTIL", "JETW", "APRN", "SIGN", "FENC",
        "MISC", "STAT", "TREE", "ROCK", "WALL", "DECK", "PIER", "CRAN", "GRND", "LGTS",
        "MALE", "ATOL",
    };

    /// <summary>
    /// Extrahiert ICAO-Kandidaten eines Add-ons. Dominanz-Kriterium gegen generische
    /// BGL-Namensteile: Der Code muss im Titel/Paketnamen stehen ODER in einem nennenswerten
    /// Anteil der BGL-Dateien vorkommen (echte Airport-Pakete prefixen ihre BGLs mit dem ICAO).
    /// </summary>
    public static IReadOnlyList<string> ExtractIcaos(string title, string packageName, IReadOnlyList<string> bglFileNames)
    {
        var nameTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ScanText(title, nameTokens);
        ScanText(packageName, nameTokens);

        var bglCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var fileName in bglFileNames)
        {
            var perFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ScanText(fileName, perFile);
            foreach (var token in perFile)
                bglCounts[token] = bglCounts.GetValueOrDefault(token) + 1;
        }

        var minDominant = Math.Max(2, (int)Math.Ceiling(bglFileNames.Count * 0.3));
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var token in nameTokens)
            found.Add(token);
        foreach (var (token, count) in bglCounts)
        {
            if (count >= minDominant)
                found.Add(token);
        }

        return found.Select(i => i.ToUpperInvariant()).OrderBy(i => i).ToList();
    }

    private static void ScanText(string text, HashSet<string> found)
    {
        foreach (Match match in TokenPattern.Matches(text))
        {
            var token = match.Groups[1].Value.ToUpperInvariant();
            if (Blacklist.Contains(token))
                continue;
            // Flugzeug-Kennungen wie "D-AGAA", "PH-BXA": GENAU 1–2 Buchstaben + Bindestrich davor.
            // Längere Präfixe ("Madeira-LPMA", "29palms-lgmk-…") sind normale Namensteile.
            var index = match.Groups[1].Index;
            if (index >= 2 && text[index - 1] == '-')
            {
                var position = index - 2;
                var prefixLength = 0;
                while (position >= 0 && char.IsLetter(text[position]))
                {
                    position--;
                    prefixLength++;
                }
                if (prefixLength is 1 or 2)
                    continue;
            }
            if (!ValidFirstLetter.Contains(token[0]))
                continue;
            if (token.Distinct().Count() == 1)
                continue;
            found.Add(token);
        }
    }

    /// <summary>ICAOs offizieller Airport-Pakete (Namensmuster "…airport-eddf-…").</summary>
    public static HashSet<string> ExtractOfficialAirportIcaos(IEnumerable<string> officialPackageNames)
    {
        var icaos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in officialPackageNames)
        {
            var match = Regex.Match(name, "airport-([a-z0-9]{4})", RegexOptions.IgnoreCase);
            if (match.Success)
                icaos.Add(match.Groups[1].Value.ToUpperInvariant());
        }
        return icaos;
    }

    /// <summary>Baut Konflikte: ein ICAO, das von mehreren Paketen (oder Paket + Official) bedient wird.</summary>
    public static IReadOnlyList<IcaoConflict> FindConflicts(
        IReadOnlyList<MsfsAddon> addons, HashSet<string> officialAirportIcaos)
    {
        // Flugzeuge, Liveries & Co. liefern nie Airports — ihre Titel produzieren nur
        // Fake-ICAOs (Kennungen wie D-AGAA, Markennamen, "100 Year"-Wörter).
        var nonAirportTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "AIRCRAFT", "LIVERY", "LIVERIES", "INSTRUMENTS", "SOUND", "EFFECT", "SIMOBJECT", "MISSION" };

        var claims = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var addon in addons)
        {
            if (nonAirportTypes.Contains(addon.ContentType))
                continue;
            // Nur Pakete, die plausibel Airports liefern (Scenery oder mit BGL-basierten ICAOs).
            if (!addon.ContentType.Equals("SCENERY", StringComparison.OrdinalIgnoreCase) && addon.Icaos.Count == 0)
                continue;
            foreach (var icao in addon.Icaos)
            {
                if (!claims.TryGetValue(icao, out var list))
                    claims[icao] = list = new List<string>();
                if (!list.Contains(addon.Title))
                    list.Add(addon.Title);
            }
        }

        var conflicts = new List<IcaoConflict>();
        foreach (var (icao, packages) in claims.OrderBy(c => c.Key))
        {
            // Tokens in 5+ Paketen sind generische Namensteile, kein realer Airport-Konflikt.
            if (packages.Count >= 5)
                continue;

            var official = officialAirportIcaos.Contains(icao);
            if (packages.Count >= 2)
                conflicts.Add(new IcaoConflict(icao, packages, official));
            else if (official)
                conflicts.Add(new IcaoConflict(icao, packages, true));
        }
        return conflicts;
    }
}
