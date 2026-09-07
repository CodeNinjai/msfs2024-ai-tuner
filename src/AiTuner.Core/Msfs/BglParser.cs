namespace AiTuner.Core.Msfs;

/// <summary>
/// Minimaler BGL-Leser: extrahiert die EXAKTEN Airport-ICAOs aus Szenerie-BGLs
/// (FSX-Format, das MSFS-Add-ons weiterhin nutzen). Liest nur Header, Sektionstabelle
/// und die Airport-Sektion (Typ 0x0003) — kein Voll-Parse. Jeder Fehler endet still
/// in einer leeren Liste; die Namens-Heuristik bleibt dann der Fallback.
/// </summary>
public static class BglParser
{
    private const uint Magic1 = 0x19920201;
    private const uint AirportSectionType = 0x0003;
    // Airport-Record-IDs: 0x003C (FSX), 0x0056 (MSFS 2020+), 0x0057 (Variante mit Anbauten).
    private static readonly HashSet<ushort> AirportRecordIds = new() { 0x003C, 0x0056, 0x0057 };

    // Prozessweiter Cache (Pfad+Größe+Änderungszeit) — der Add-on-Rescan nach Bibliotheks-
    // Aktionen soll nicht jedes Mal alle BGLs neu lesen.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Size, DateTime Mtime, List<string> Icaos)>
        Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Alle Airport-ICAOs einer BGL-Datei (leer bei Nicht-Airport-BGLs oder Fehlern).</summary>
    public static List<string> ReadAirportIcaos(string bglPath)
    {
        try
        {
            var info = new FileInfo(bglPath);
            if (Cache.TryGetValue(bglPath, out var cached)
                && cached.Size == info.Length && cached.Mtime == info.LastWriteTimeUtc)
                return cached.Icaos;
            var icaos = ReadAirportIcaosUncached(bglPath);
            Cache[bglPath] = (info.Length, info.LastWriteTimeUtc, icaos);
            return icaos;
        }
        catch
        {
            return new List<string>();
        }
    }

    private static List<string> ReadAirportIcaosUncached(string bglPath)
    {
        var result = new List<string>();
        try
        {
            using var stream = File.OpenRead(bglPath);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 0x38 + 0x14)
                return result;

            if (reader.ReadUInt32() != Magic1)
                return result;
            stream.Position = 0x14;
            var sectionCount = reader.ReadUInt32();
            if (sectionCount > 1024)
                return result;

            for (var sectionIndex = 0; sectionIndex < sectionCount; sectionIndex++)
            {
                stream.Position = 0x38 + sectionIndex * 0x14;
                var type = reader.ReadUInt32();
                var sizeFlag = reader.ReadUInt32();
                var subsectionCount = reader.ReadUInt32();
                var subsectionOffset = reader.ReadUInt32();
                reader.ReadUInt32(); // totalSubsectionSize

                if (type != AirportSectionType || subsectionCount > 4096)
                    continue;

                // Subsection-Eintragsgröße: 16 oder 20 Bytes (dokumentierte Flag-Formel).
                var subsectionEntrySize = (int)(((sizeFlag & 0x10000) | 0x40000) >> 14);

                for (var subIndex = 0; subIndex < subsectionCount; subIndex++)
                {
                    var entryPosition = subsectionOffset + (long)subIndex * subsectionEntrySize;
                    if (entryPosition + 16 > stream.Length)
                        break;
                    stream.Position = entryPosition + 4; // qmid überspringen
                    var recordCount = reader.ReadUInt32();
                    var dataOffset = reader.ReadUInt32();
                    var dataSize = reader.ReadUInt32();
                    if (recordCount == 0 || recordCount > 4096 || dataOffset + (long)dataSize > stream.Length)
                        continue;

                    var position = (long)dataOffset;
                    for (var recordIndex = 0; recordIndex < recordCount && position + 6 <= dataOffset + dataSize; recordIndex++)
                    {
                        stream.Position = position;
                        var recordId = reader.ReadUInt16();
                        var recordSize = reader.ReadUInt32();
                        if (recordSize < 6 || position + recordSize > dataOffset + dataSize + 16)
                            break;

                        if (AirportRecordIds.Contains(recordId) && recordSize >= 0x2C)
                        {
                            stream.Position = position + 0x28; // dokumentierte ICAO-Position
                            var icao = DecodeIdent(reader.ReadUInt32());
                            if (icao.Length >= 2 && icao.Length <= 4
                                && char.IsLetter(icao[0])
                                && icao.All(char.IsLetterOrDigit)
                                && !result.Contains(icao))
                                result.Add(icao);
                        }
                        position += recordSize;
                    }
                }
            }
        }
        catch { }
        return result;
    }

    /// <summary>Dekodiert die Base-38-Kodierung von Ident-Feldern (ICAO/Region).</summary>
    private static string DecodeIdent(uint value)
    {
        value >>= 5;
        var chars = new List<char>();
        while (value > 0)
        {
            var digit = value % 38;
            value /= 38;
            chars.Insert(0, digit switch
            {
                0 => ' ',
                >= 2 and <= 11 => (char)('0' + digit - 2),
                >= 12 and <= 37 => (char)('A' + digit - 12),
                _ => '?',
            });
        }
        return new string(chars.ToArray()).Trim();
    }
}
