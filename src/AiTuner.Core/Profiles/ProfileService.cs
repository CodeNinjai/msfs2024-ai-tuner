using System.Globalization;
using System.Text.Json;
using AiTuner.Core.Apply;
using Microsoft.Win32;

namespace AiTuner.Core.Profiles;

public sealed record NvidiaProfileValue(string Id, long Value);

/// <summary>A saved settings state: Windows registry values, power plan, UserCfg copy, NVIDIA profile.</summary>
public sealed class SettingsProfile
{
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public bool IsBackup { get; set; }
    public string? MsfsEdition { get; set; }
    public bool HasUserCfg { get; set; }
    public Dictionary<string, int?> WindowsValues { get; set; } = new();
    public string? PowerPlanGuid { get; set; }
    public List<NvidiaProfileValue> NvidiaSettings { get; set; } = new();
}

public static class ProfileService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Semantic key → (isLocalMachine, registry path, value name).</summary>
    private static readonly Dictionary<string, (bool Hklm, string Path, string Name)> WindowsKeys = new()
    {
        ["gamedvr"] = (false, @"System\GameConfigStore", "GameDVR_Enabled"),
        ["historicalcapture"] = (false, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled"),
        ["gamebarcapture"] = (false, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled"),
        ["gamemode"] = (false, @"Software\Microsoft\GameBar", "AutoGameModeEnabled"),
        ["hags"] = (true, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode"),
        ["mpo"] = (true, @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode"),
    };

    public static string RootDirectory
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Msfs2024AiTuner", "profiles");

    public static string DirectoryFor(string name) => Path.Combine(RootDirectory, Sanitize(name));

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
    }

    public static SettingsProfile Capture(AnalysisResult analysis, string name, bool isBackup)
    {
        var profile = new SettingsProfile
        {
            Name = name,
            CreatedAt = DateTimeOffset.Now,
            IsBackup = isBackup,
        };

        foreach (var (key, (hklm, path, valueName)) in WindowsKeys)
            profile.WindowsValues[key] = WindowsApplier.ReadDword(hklm ? Registry.LocalMachine : Registry.CurrentUser, path, valueName);

        profile.PowerPlanGuid = analysis.WindowsSettings.Items
            .FirstOrDefault(i => i.Key == "powerplan")?.Detail;

        foreach (var setting in analysis.Nvidia.MsfsSettings)
        {
            if (setting.Numeric is { } numeric)
                profile.NvidiaSettings.Add(new NvidiaProfileValue(setting.Id, numeric));
        }

        var directory = DirectoryFor(name);
        Directory.CreateDirectory(directory);

        var installation = analysis.MsfsInstallations.FirstOrDefault(i => i.UserCfgExists);
        if (installation is not null)
        {
            profile.MsfsEdition = installation.EditionName;
            try
            {
                // Kein File.Copy: Die Store-UserCfg ist EFS-verschlüsselt, das Kopieren mit
                // Verschlüsselungsattribut schlägt fehl — Inhalt lesen und neu schreiben.
                File.WriteAllBytes(Path.Combine(directory, "UserCfg.opt"), File.ReadAllBytes(installation.UserCfgPath));
                profile.HasUserCfg = true;
            }
            catch { }
        }

        File.WriteAllText(Path.Combine(directory, "profile.json"),
            JsonSerializer.Serialize(profile, JsonOptions));
        return profile;
    }

    /// <summary>
    /// Erzeugt ein Experiment-Profil OFFLINE: aktueller Stand + die Änderung(en) der Aktion
    /// hineingerechnet — ohne das Live-System anzufassen.
    /// </summary>
    public static SettingsProfile CaptureExperiment(AnalysisResult analysis, string name, string actionJson)
    {
        var profile = Capture(analysis, name, isBackup: false);
        PatchWithAction(profile, actionJson);
        File.WriteAllText(System.IO.Path.Combine(DirectoryFor(name), "profile.json"),
            JsonSerializer.Serialize(profile, JsonOptions));
        return profile;
    }

    private static void PatchWithAction(SettingsProfile profile, string actionJson)
    {
        using var document = System.Text.Json.JsonDocument.Parse(actionJson);
        PatchWithAction(profile, document.RootElement);
    }

    private static void PatchWithAction(SettingsProfile profile, System.Text.Json.JsonElement action)
    {
        var type = action.GetProperty("type").GetString() ?? "";
        switch (type)
        {
            case "multi":
                foreach (var sub in action.GetProperty("actions").EnumerateArray())
                    PatchWithAction(profile, sub);
                break;
            case "msfs":
            {
                var userCfgPath = System.IO.Path.Combine(DirectoryFor(profile.Name), "UserCfg.opt");
                if (!File.Exists(userCfgPath))
                    break;
                var cfg = Msfs.UserCfgDocument.Parse(File.ReadAllText(userCfgPath));
                cfg.SetValue(action.GetProperty("section").GetString() ?? "",
                    action.GetProperty("key").GetString() ?? "",
                    action.GetProperty("value").GetString() ?? "");
                File.WriteAllText(userCfgPath, cfg.ToText(), new System.Text.UTF8Encoding(false));
                break;
            }
            case "reg":
            {
                var hklm = (action.GetProperty("hive").GetString() ?? "HKCU")
                    .Equals("HKLM", StringComparison.OrdinalIgnoreCase);
                var path = action.GetProperty("path").GetString() ?? "";
                var valueName = action.GetProperty("name").GetString() ?? "";
                foreach (var (key, target) in WindowsKeys)
                {
                    if (target.Hklm == hklm
                        && target.Path.Equals(path, StringComparison.OrdinalIgnoreCase)
                        && target.Name.Equals(valueName, StringComparison.OrdinalIgnoreCase))
                    {
                        profile.WindowsValues[key] = action.GetProperty("value").GetInt32();
                        break;
                    }
                }
                break;
            }
            case "powerplan":
                profile.PowerPlanGuid = action.GetProperty("guid").GetString();
                break;
            case "nvidia":
            {
                var id = Convert.ToUInt32(action.GetProperty("settingId").GetString() ?? "0", 16);
                var idText = $"0x{id:X8}";
                profile.NvidiaSettings.RemoveAll(s =>
                    s.Id.Equals(idText, StringComparison.OrdinalIgnoreCase));
                profile.NvidiaSettings.Add(new NvidiaProfileValue(idText, action.GetProperty("value").GetUInt32()));
                break;
            }
        }
    }

    public static IReadOnlyList<SettingsProfile> List()
    {
        var profiles = new List<SettingsProfile>();
        try
        {
            if (!Directory.Exists(RootDirectory))
                return profiles;

            foreach (var directory in Directory.EnumerateDirectories(RootDirectory))
            {
                var file = Path.Combine(directory, "profile.json");
                if (!File.Exists(file))
                    continue;
                try
                {
                    var profile = JsonSerializer.Deserialize<SettingsProfile>(File.ReadAllText(file));
                    if (profile is not null)
                        profiles.Add(profile);
                }
                catch { }
            }
        }
        catch { }
        return profiles.OrderByDescending(p => p.CreatedAt).ToList();
    }

    public static (bool Ok, string Message) Delete(string name)
    {
        try
        {
            var directory = DirectoryFor(name);
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
            return (true, $"„{name}“ gelöscht.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Applies a saved profile. Returns a per-step log; HKLM steps trigger UAC prompts.</summary>
    public static (bool AllOk, List<string> Log) Apply(SettingsProfile profile, AnalysisResult analysis)
    {
        var log = new List<string>();
        var allOk = true;

        foreach (var (key, storedValue) in profile.WindowsValues)
        {
            if (!WindowsKeys.TryGetValue(key, out var target))
                continue;

            var currentValue = WindowsApplier.ReadDword(
                target.Hklm ? Registry.LocalMachine : Registry.CurrentUser, target.Path, target.Name);
            if (currentValue == storedValue)
                continue;

            (bool Ok, string Message) result;
            if (storedValue is { } value)
            {
                result = target.Hklm
                    ? WindowsApplier.SetLocalMachineDword(target.Path, target.Name, value)
                    : WindowsApplier.SetCurrentUserDword(target.Path, target.Name, value);
            }
            else
            {
                result = target.Hklm
                    ? WindowsApplier.DeleteLocalMachineValue(target.Path, target.Name)
                    : WindowsApplier.DeleteCurrentUserValue(target.Path, target.Name);
            }
            allOk &= result.Ok;
            log.Add($"[{(result.Ok ? "OK" : "FEHLER")}] Windows/{key}: {result.Message}");
        }

        if (profile.PowerPlanGuid is not null && Guid.TryParse(profile.PowerPlanGuid, out var planGuid))
        {
            var current = analysis.WindowsSettings.Items.FirstOrDefault(i => i.Key == "powerplan")?.Detail;
            if (!string.Equals(current, profile.PowerPlanGuid, StringComparison.OrdinalIgnoreCase))
            {
                var (ok, message) = WindowsApplier.SetActivePowerPlan(planGuid);
                allOk &= ok;
                log.Add($"[{(ok ? "OK" : "FEHLER")}] Energieplan: {message}");
            }
        }

        if (profile.HasUserCfg)
        {
            var installation = analysis.MsfsInstallations.FirstOrDefault(i => i.UserCfgExists);
            var backupFile = Path.Combine(DirectoryFor(profile.Name), "UserCfg.opt");
            if (installation is not null)
            {
                var (ok, message) = MsfsApplier.RestoreUserCfg(installation, backupFile);
                allOk &= ok;
                log.Add($"[{(ok ? "OK" : "FEHLER")}] MSFS: {message}");
            }
        }

        if (profile.NvidiaSettings.Count > 0)
        {
            var settings = new List<(uint, uint)>();
            foreach (var entry in profile.NvidiaSettings)
            {
                try
                {
                    settings.Add((Convert.ToUInt32(entry.Id, 16), unchecked((uint)entry.Value)));
                }
                catch { }
            }
            var (ok, message) = NvidiaApplier.RestoreMsfsProfile(settings);
            allOk &= ok;
            log.Add($"[{(ok ? "OK" : "FEHLER")}] NVIDIA: {message}");
        }

        if (log.Count == 0)
            log.Add("Keine Abweichungen — alle Werte entsprechen bereits dem Profil.");

        return (allOk, log);
    }
}
