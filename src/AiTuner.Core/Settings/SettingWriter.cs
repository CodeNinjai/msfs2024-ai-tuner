using System.Globalization;
using System.Text.Json.Nodes;
using AiTuner.Core.Apply;

namespace AiTuner.Core.Settings;

/// <summary>Injects the chosen value into the setting's action template and executes it.</summary>
public static class SettingWriter
{
    public static ApplyOutcome Write(SettingDefinition definition, string value, AnalysisResult analysis)
    {
        try
        {
            var node = JsonNode.Parse(definition.Target.GetRawText())!.AsObject();
            var type = node["type"]?.GetValue<string>() ?? "";

            switch (type)
            {
                case "msfs":
                    node["value"] = value;
                    break;
                case "reg":
                    node["value"] = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "nvidia":
                    node["value"] = uint.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "powerplan":
                    node["guid"] = value;
                    break;
                default:
                    return new ApplyOutcome(false, $"Unbekannter Zieltyp „{type}“ im Katalogeintrag {definition.Id}.");
            }

            return ActionExecutor.Execute(node.ToJsonString(), analysis);
        }
        catch (Exception ex)
        {
            return new ApplyOutcome(false, "Schreiben fehlgeschlagen: " + ex.Message);
        }
    }
}
