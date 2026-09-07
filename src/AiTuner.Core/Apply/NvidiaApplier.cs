using AiTuner.Core.Nvidia;
using NvAPIWrapper;
using NvAPIWrapper.DRS;

namespace AiTuner.Core.Apply;

/// <summary>Writes NVIDIA driver-profile settings (DRS) into the MSFS application profile.</summary>
public static class NvidiaApplier
{
    public static (bool Ok, string Message) SetMsfsProfileDword(uint settingId, uint value)
    {
        try
        {
            NVIDIA.Initialize();
            var session = DriverSettingsSession.CreateAndLoad();
            try
            {
                var profile = NvidiaReader.FindMsfsProfile(session, new List<string>());
                if (profile is null)
                    return (false, "Kein MSFS-Profil im Treiber gefunden — den Sim einmal starten, damit der Treiber das Profil anlegt.");

                profile.SetSetting(settingId, value);
                session.Save();
                return (true, $"Treiberprofil „{profile.Name}“: 0x{settingId:X8} = {value} gesetzt.");
            }
            finally
            {
                session.Dispose();
            }
        }
        catch (Exception ex)
        {
            return (false, "NVAPI-Fehler: " + ex.Message);
        }
    }

    /// <summary>Restores a list of numeric profile settings (used by profile/backup restore).</summary>
    public static (bool Ok, string Message) RestoreMsfsProfile(IEnumerable<(uint Id, uint Value)> settings)
    {
        try
        {
            NVIDIA.Initialize();
            var session = DriverSettingsSession.CreateAndLoad();
            try
            {
                var profile = NvidiaReader.FindMsfsProfile(session, new List<string>());
                if (profile is null)
                    return (false, "Kein MSFS-Profil im Treiber gefunden.");

                var count = 0;
                foreach (var (id, value) in settings)
                {
                    profile.SetSetting(id, value);
                    count++;
                }
                session.Save();
                return (true, $"{count} Treiberprofil-Einstellungen wiederhergestellt.");
            }
            finally
            {
                session.Dispose();
            }
        }
        catch (Exception ex)
        {
            return (false, "NVAPI-Fehler: " + ex.Message);
        }
    }
}
