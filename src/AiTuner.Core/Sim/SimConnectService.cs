using System.Runtime.InteropServices;

namespace AiTuner.Core.Sim;

/// <summary>Momentaufnahme der Sim-Situation (für Benchmarks, Tuning-Assistent, Monitor).</summary>
public sealed record SimSnapshot(
    double Latitude,
    double Longitude,
    double AltitudeFt,
    double IndicatedAirspeedKt,
    bool OnGround,
    string AircraftTitle)
{
    public string PositionText => $"{Latitude:0.0000}, {Longitude:0.0000} · {AltitudeFt:0} ft · {IndicatedAirspeedKt:0} kt IAS";
    public string PhaseText => OnGround ? "am Boden" : "airborne";

    /// <summary>Grobe Distanz in km (Kugel-Näherung) — reicht für „gleicher Messort?“.</summary>
    public double DistanceKmTo(double latitude, double longitude)
    {
        const double earthRadiusKm = 6371;
        var dLat = (latitude - Latitude) * Math.PI / 180;
        var dLon = (longitude - Longitude) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(Latitude * Math.PI / 180) * Math.Cos(latitude * Math.PI / 180)
                * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * earthRadiusKm * Math.Asin(Math.Sqrt(a));
    }
}

/// <summary>
/// SimConnect direkt über die native C-API (P/Invoke auf SimConnect.dll) — die offizielle
/// Managed-Bibliothek ist Mixed-Mode-.NET-Framework und unter .NET 8 nicht ladbar.
/// Verbindet on demand, pollt per GetNextDispatch; alle Fehler enden still in „nicht verbunden“.
/// </summary>
public sealed class SimConnectService : IDisposable
{
    private const uint DefinitionId = 1;
    private const uint RequestId = 1;
    private const uint RecvIdQuit = 3;
    private const uint RecvIdSimobjectDataByType = 9;
    private const uint DatatypeFloat64 = 4;
    private const uint DatatypeString256 = 9;
    private const uint SimObjectTypeUser = 0;
    private const uint SimConnectUnused = 0xFFFFFFFF;

    [DllImport("SimConnect.dll", CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern int SimConnect_Open(out IntPtr handle, string name, IntPtr hwnd,
        uint userEventWin32, IntPtr eventHandle, uint configIndex);

    [DllImport("SimConnect.dll")]
    private static extern int SimConnect_Close(IntPtr handle);

    [DllImport("SimConnect.dll", CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern int SimConnect_AddToDataDefinition(IntPtr handle, uint defineId,
        string datumName, string? unitsName, uint datumType, float epsilon, uint datumId);

    [DllImport("SimConnect.dll")]
    private static extern int SimConnect_RequestDataOnSimObjectType(IntPtr handle, uint requestId,
        uint defineId, uint radiusMeters, uint type);

    [DllImport("SimConnect.dll")]
    private static extern int SimConnect_GetNextDispatch(IntPtr handle, out IntPtr data, out uint size);

    private IntPtr _handle = IntPtr.Zero;

    public bool IsConnected => _handle != IntPtr.Zero;

    /// <summary>Verbindet zum laufenden Sim; false, wenn keiner erreichbar ist (kein Fehler).</summary>
    public bool TryConnect()
    {
        if (IsConnected)
            return true;
        try
        {
            if (SimConnect_Open(out var handle, "MSFS 2024 AI Tuner", IntPtr.Zero, 0, IntPtr.Zero, 0) < 0
                || handle == IntPtr.Zero)
                return false;
            _handle = handle;

            // Reihenfolge = Speicherlayout der Antwort (5× FLOAT64, dann STRING256).
            SimConnect_AddToDataDefinition(_handle, DefinitionId, "PLANE LATITUDE", "degrees", DatatypeFloat64, 0, SimConnectUnused);
            SimConnect_AddToDataDefinition(_handle, DefinitionId, "PLANE LONGITUDE", "degrees", DatatypeFloat64, 0, SimConnectUnused);
            SimConnect_AddToDataDefinition(_handle, DefinitionId, "PLANE ALTITUDE", "feet", DatatypeFloat64, 0, SimConnectUnused);
            SimConnect_AddToDataDefinition(_handle, DefinitionId, "AIRSPEED INDICATED", "knots", DatatypeFloat64, 0, SimConnectUnused);
            SimConnect_AddToDataDefinition(_handle, DefinitionId, "SIM ON GROUND", "bool", DatatypeFloat64, 0, SimConnectUnused);
            SimConnect_AddToDataDefinition(_handle, DefinitionId, "TITLE", null, DatatypeString256, 0, SimConnectUnused);
            return true;
        }
        catch
        {
            Disconnect();
            return false;
        }
    }

    /// <summary>Fordert eine frische Momentaufnahme an und pollt kurz auf die Antwort.</summary>
    public SimSnapshot? Poll(int timeoutMs = 1500)
    {
        try
        {
            if (!IsConnected && !TryConnect())
                return null;

            if (SimConnect_RequestDataOnSimObjectType(_handle, RequestId, DefinitionId, 0, SimObjectTypeUser) < 0)
                return null;

            var waited = 0;
            while (waited < timeoutMs)
            {
                while (SimConnect_GetNextDispatch(_handle, out var data, out var size) >= 0 && data != IntPtr.Zero)
                {
                    var snapshot = ParseDispatch(data, size);
                    if (snapshot is not null)
                        return snapshot;
                }
                Thread.Sleep(25);
                waited += 25;
            }
            return null;
        }
        catch
        {
            Disconnect();
            return null;
        }
    }

    private SimSnapshot? ParseDispatch(IntPtr data, uint size)
    {
        // SIMCONNECT_RECV: dwSize(0) dwVersion(4) dwID(8)
        var id = (uint)Marshal.ReadInt32(data, 8);
        if (id == RecvIdQuit)
        {
            Disconnect();
            return null;
        }
        if (id != RecvIdSimobjectDataByType)
            return null;

        // SIMCONNECT_RECV_SIMOBJECT_DATA: RECV(12) + RequestID ObjectID DefineID Flags
        // entrynumber outof DefineCount (7×4) → Nutzdaten ab Offset 40.
        var requestId = (uint)Marshal.ReadInt32(data, 12);
        if (requestId != RequestId || size < 40 + 5 * 8 + 1)
            return null;

        var payload = data + 40;
        var latitude = ReadDouble(payload, 0);
        var longitude = ReadDouble(payload, 8);
        var altitude = ReadDouble(payload, 16);
        var airspeed = ReadDouble(payload, 24);
        var onGround = ReadDouble(payload, 32) != 0;
        var titleBytes = Math.Min(256, (int)size - 40 - 40);
        var title = titleBytes > 0 ? (Marshal.PtrToStringAnsi(payload + 40, titleBytes) ?? "") : "";
        var nul = title.IndexOf('\0');
        if (nul >= 0)
            title = title[..nul];

        return new SimSnapshot(latitude, longitude, altitude, airspeed, onGround, title.Trim());
    }

    private static double ReadDouble(IntPtr basePointer, int offset)
        => BitConverter.Int64BitsToDouble(Marshal.ReadInt64(basePointer, offset));

    private void Disconnect()
    {
        if (_handle != IntPtr.Zero)
        {
            try { SimConnect_Close(_handle); } catch { }
            _handle = IntPtr.Zero;
        }
    }

    public void Dispose() => Disconnect();
}
