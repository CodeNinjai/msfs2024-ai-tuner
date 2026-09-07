namespace AiTuner.Core.Msfs;

public enum MsfsEdition
{
    MicrosoftStore,
    Steam,
}

public sealed class MsfsInstallation
{
    public required MsfsEdition Edition { get; init; }
    public required string UserCfgPath { get; init; }

    public string EditionName => Edition == MsfsEdition.MicrosoftStore ? "Microsoft Store" : "Steam";
    public bool UserCfgExists => File.Exists(UserCfgPath);

    public UserCfgDocument? LoadUserCfg()
    {
        try
        {
            return UserCfgExists ? UserCfgDocument.Parse(File.ReadAllText(UserCfgPath)) : null;
        }
        catch
        {
            return null;
        }
    }
}

public static class MsfsLocator
{
    /// <summary>Returns both known MSFS 2024 install candidates; check UserCfgExists per entry.</summary>
    public static IReadOnlyList<MsfsInstallation> DetectInstallations()
    {
        var storePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalCache", "UserCfg.opt");

        var steamPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft Flight Simulator 2024", "UserCfg.opt");

        return
        [
            new MsfsInstallation { Edition = MsfsEdition.MicrosoftStore, UserCfgPath = storePath },
            new MsfsInstallation { Edition = MsfsEdition.Steam, UserCfgPath = steamPath },
        ];
    }
}
