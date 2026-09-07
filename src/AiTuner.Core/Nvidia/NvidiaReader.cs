using NvAPIWrapper;
using NvAPIWrapper.DRS;

namespace AiTuner.Core.Nvidia;

public sealed record NvidiaDrsSetting(string Name, string Id, string Value, long? Numeric = null);

public sealed class NvidiaSnapshot
{
    public bool Available { get; init; }
    public string? MsfsProfileName { get; init; }
    public IReadOnlyList<NvidiaDrsSetting> GlobalSettings { get; init; } = [];
    public IReadOnlyList<NvidiaDrsSetting> MsfsSettings { get; init; } = [];
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// Reads NVIDIA driver profiles (DRS) via NVAPI — the same data NVIDIA Profile Inspector shows.
/// Read-only in stage 0.
/// </summary>
public static class NvidiaReader
{
    private static readonly string[] MsfsExecutables =
    [
        "flightsimulator2024.exe",
        "flightsimulator.exe",
    ];

    public static NvidiaSnapshot Read()
    {
        var warnings = new List<string>();
        try
        {
            NVIDIA.Initialize();
            var session = DriverSettingsSession.CreateAndLoad();
            try
            {
                var globalSettings = ReadProfileSettings(session.CurrentGlobalProfile);

                string? msfsProfileName = null;
                var msfsSettings = new List<NvidiaDrsSetting>();
                var msfsProfile = FindMsfsProfile(session, warnings);
                if (msfsProfile is not null)
                {
                    msfsProfileName = msfsProfile.Name;
                    msfsSettings = ReadProfileSettings(msfsProfile);
                }

                var snapshot = new NvidiaSnapshot
                {
                    Available = true,
                    MsfsProfileName = msfsProfileName,
                    GlobalSettings = globalSettings,
                    MsfsSettings = msfsSettings,
                };
                snapshot.Warnings.AddRange(warnings);
                return snapshot;
            }
            finally
            {
                session.Dispose();
            }
        }
        catch (Exception ex)
        {
            var snapshot = new NvidiaSnapshot { Available = false };
            snapshot.Warnings.Add("NVIDIA-Treiberprofile nicht lesbar: " + ex.Message);
            return snapshot;
        }
    }

    private static List<NvidiaDrsSetting> ReadProfileSettings(DriverSettingsProfile profile)
    {
        var list = new List<NvidiaDrsSetting>();
        foreach (var setting in profile.Settings)
        {
            var id = (uint)setting.SettingId;
            string name;
            try
            {
                name = setting.SettingInfo?.Name ?? $"0x{id:X8}";
            }
            catch
            {
                name = $"0x{id:X8}";
            }
            long? numeric = setting.CurrentValue switch
            {
                uint u => u,
                int i => i,
                _ => null,
            };
            list.Add(new NvidiaDrsSetting(name, $"0x{id:X8}", FormatValue(setting.CurrentValue), numeric));
        }
        return list;
    }

    internal static DriverSettingsProfile? FindMsfsProfile(DriverSettingsSession session, List<string> warnings)
    {
        foreach (var executable in MsfsExecutables)
        {
            try
            {
                var application = session.FindApplication(executable);
                if (application?.Profile is not null)
                    return application.Profile;
            }
            catch
            {
                // Anwendung nicht registriert — nächsten Kandidaten versuchen.
            }
        }

        try
        {
            foreach (var profile in session.Profiles)
            {
                if (profile.Name.Contains("Flight Simulator 2024", StringComparison.OrdinalIgnoreCase))
                    return profile;
            }
        }
        catch (Exception ex)
        {
            warnings.Add("Profilsuche: " + ex.Message);
        }

        return null;
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "—",
        uint u => $"{u} (0x{u:X8})",
        int i => $"{i} (0x{i:X8})",
        string s => s,
        byte[] bytes => Convert.ToHexString(bytes),
        _ => value.ToString() ?? "—",
    };
}
