using System.Text.Json;

namespace AiTuner.Core.Apply;

public sealed record ApplyOutcome(bool Ok, string Message, bool NeedsReboot = false, bool NeedsSimRestart = false);

/// <summary>
/// Executes the declarative "action" block of a rule:
///   {"type":"reg","hive":"HKCU","path":"…","name":"…","value":0[,"reboot":true]}
///   {"type":"regdelete","hive":"HKLM","path":"…","name":"…"}
///   {"type":"powerplan","guid":"381b4222-…"}
///   {"type":"msfs","section":"Video","key":"VSync","value":"0"}
///   {"type":"nvidia","settingId":"0x1057EB71","value":1}
/// </summary>
public static class ActionExecutor
{
    public static ApplyOutcome Execute(string actionJson, AnalysisResult analysis)
    {
        try
        {
            using var document = JsonDocument.Parse(actionJson);
            var action = document.RootElement;
            var type = action.GetProperty("type").GetString() ?? "";
            var needsReboot = action.TryGetProperty("reboot", out var reboot) && reboot.GetBoolean();

            switch (type)
            {
                case "reg":
                {
                    var hive = action.GetProperty("hive").GetString() ?? "HKCU";
                    var path = action.GetProperty("path").GetString() ?? "";
                    var name = action.GetProperty("name").GetString() ?? "";
                    var value = action.GetProperty("value").GetInt32();
                    var (ok, message) = hive.Equals("HKLM", StringComparison.OrdinalIgnoreCase)
                        ? WindowsApplier.SetLocalMachineDword(path, name, value)
                        : WindowsApplier.SetCurrentUserDword(path, name, value);
                    return new ApplyOutcome(ok, message, ok && needsReboot);
                }
                case "regdelete":
                {
                    var hive = action.GetProperty("hive").GetString() ?? "HKCU";
                    var path = action.GetProperty("path").GetString() ?? "";
                    var name = action.GetProperty("name").GetString() ?? "";
                    var (ok, message) = hive.Equals("HKLM", StringComparison.OrdinalIgnoreCase)
                        ? WindowsApplier.DeleteLocalMachineValue(path, name)
                        : WindowsApplier.DeleteCurrentUserValue(path, name);
                    return new ApplyOutcome(ok, message, ok && needsReboot);
                }
                case "powerplan":
                {
                    var guid = Guid.Parse(action.GetProperty("guid").GetString() ?? "");
                    var (ok, message) = WindowsApplier.SetActivePowerPlan(guid);
                    return new ApplyOutcome(ok, message);
                }
                case "msfs":
                {
                    var installation = analysis.MsfsInstallations.FirstOrDefault(i => i.UserCfgExists);
                    if (installation is null)
                        return new ApplyOutcome(false, "Keine MSFS-Installation mit UserCfg.opt gefunden.");
                    var section = action.GetProperty("section").GetString() ?? "";
                    var key = action.GetProperty("key").GetString() ?? "";
                    var value = action.GetProperty("value").GetString() ?? "";
                    var (ok, message) = MsfsApplier.SetValue(installation, section, key, value);
                    return new ApplyOutcome(ok, message, NeedsSimRestart: ok);
                }
                case "nvidia":
                {
                    var idText = action.GetProperty("settingId").GetString() ?? "0";
                    var settingId = Convert.ToUInt32(idText, 16);
                    var value = action.GetProperty("value").GetUInt32();
                    var (ok, message) = NvidiaApplier.SetMsfsProfileDword(settingId, value);
                    return new ApplyOutcome(ok, message);
                }
                case "multi":
                {
                    var messages = new List<string>();
                    var allOk = true;
                    var needsRebootAny = false;
                    var needsSimRestart = false;
                    foreach (var subAction in action.GetProperty("actions").EnumerateArray())
                    {
                        var outcome = Execute(subAction.GetRawText(), analysis);
                        allOk &= outcome.Ok;
                        needsRebootAny |= outcome.NeedsReboot;
                        needsSimRestart |= outcome.NeedsSimRestart;
                        messages.Add((outcome.Ok ? "✓ " : "✗ ") + outcome.Message);
                    }
                    return new ApplyOutcome(allOk, string.Join("\n", messages), needsRebootAny, needsSimRestart);
                }
                default:
                    return new ApplyOutcome(false, $"Unbekannter Aktionstyp „{type}“.");
            }
        }
        catch (Exception ex)
        {
            return new ApplyOutcome(false, "Aktion fehlgeschlagen: " + ex.Message);
        }
    }
}
