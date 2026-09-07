using AiTuner.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiTuner.App.ViewModels;

/// <summary>One line of the value-range table in the ?-bubble; Value=null renders without chip.</summary>
public sealed record RangeLine(string? Value, string Text, bool IsNote = false)
{
    public bool HasValue => Value is not null;
}

/// <summary>
/// Editable row in the settings lists, backed by a catalog entry. Enum changes apply
/// immediately (guarded against the initial population); numeric values apply via button.
/// </summary>
public sealed partial class EditableSettingRow : ObservableObject
{
    public SettingDefinition Definition { get; }

    public string Name => Definition.Name;
    public string Description => Definition.Description;
    public string Kind => Definition.Kind;

    public string LoadText => Definition.LoadLabel;
    public string ImpactText => Definition.ImpactLabel;
    public string ImpactLevel => Definition.Impact?.ToLowerInvariant() ?? "";
    public bool HasLoad => LoadText.Length > 0;
    public bool HasImpact => ImpactText.Length > 0;
    public bool HasMeta => HasLoad || HasImpact;

    /// <summary>Zeile für die ?-Bubble, z.B. „Belastet: GPU · FPS-Impact: Sehr hoch".</summary>
    public string MetaText => string.Join("   ·   ", new[]
    {
        HasLoad ? $"Belastet: {LoadText}" : null,
        HasImpact ? $"FPS-Impact: {ImpactText}" : null,
    }.Where(s => s is not null));

    /// <summary>Wertebereich zeilenweise für die ?-Bubble (eine Option pro Zeile).</summary>
    public IReadOnlyList<RangeLine> RangeLines { get; }

    public List<SettingOption> Options { get; }

    [ObservableProperty] private SettingOption? _selectedOption;
    [ObservableProperty] private string _numberText = "";

    private readonly Func<EditableSettingRow, string, Task> _apply;
    private readonly bool _decimalFormat;
    private bool _initialized;
    private SettingOption? _confirmedOption;

    /// <summary>False, wenn der übergeordnete Schalter (dependsOn) das Setting deaktiviert — Zeile wird gesperrt/gedimmt.</summary>
    public bool IsRowEnabled { get; }

    public EditableSettingRow(SettingDefinition definition, string currentRawValue,
        Func<EditableSettingRow, string, Task> apply, bool isRowEnabled = true)
    {
        Definition = definition;
        _apply = apply;
        IsRowEnabled = isRowEnabled;

        var rangeLines = new List<RangeLine>();
        if (definition.Kind == "number")
        {
            if (definition.Min is not null && definition.Max is not null)
                rangeLines.Add(new RangeLine(null, $"Wertebereich: {definition.Min} – {definition.Max}"));
        }
        else
        {
            rangeLines.AddRange(definition.Options.Select(o => new RangeLine(o.Value, o.Label)));
        }
        if (!string.IsNullOrWhiteSpace(definition.RangeNote))
            rangeLines.Add(new RangeLine(null, definition.RangeNote!, IsNote: true));
        RangeLines = rangeLines;

        var current = currentRawValue.Trim().Trim('"');
        Options = new List<SettingOption>(definition.Options);

        if (definition.Kind == "enum")
        {
            var match = Options.FirstOrDefault(o =>
                string.Equals(o.Value, current, StringComparison.OrdinalIgnoreCase)
                || (double.TryParse(o.Value, out var a) && double.TryParse(current, out var b) && Math.Abs(a - b) < 0.0001));
            if (match is null)
            {
                match = new SettingOption(current, $"{current} (aktueller Wert)");
                Options.Insert(0, match);
            }
            _selectedOption = match;
            _confirmedOption = match;
        }
        else
        {
            _numberText = current;
            _decimalFormat = current.Contains('.');
        }

        _initialized = true;
    }

    partial void OnSelectedOptionChanged(SettingOption? value)
    {
        if (_initialized && value is not null)
            _ = _apply(this, value.Value);
    }

    /// <summary>Nach erfolgreichem Schreiben aufrufen — der aktuelle Wert gilt als bestätigt.</summary>
    public void ConfirmSelection() => _confirmedOption = SelectedOption;

    /// <summary>Setzt das Dropdown nach fehlgeschlagenem Schreiben auf den letzten bestätigten Wert zurück.</summary>
    public void RevertSelection()
    {
        if (Kind != "enum")
            return;
        _initialized = false;
        SelectedOption = _confirmedOption;
        _initialized = true;
    }

    [RelayCommand]
    private async Task ApplyNumberAsync()
    {
        var text = NumberText.Trim().Replace(',', '.');
        if (!double.TryParse(text, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            Controls.AppDialog.Warn(Name, "Bitte eine Zahl eingeben.");
            return;
        }
        if ((Definition.Min is { } min && number < min) || (Definition.Max is { } max && number > max))
        {
            Controls.AppDialog.Warn(Name, $"Wert außerhalb des Bereichs ({Definition.Min} – {Definition.Max}).");
            return;
        }

        // Format des bisherigen Werts beibehalten: Faktoren mit 6 Nachkommastellen, sonst ganzzahlig.
        var value = _decimalFormat
            ? number.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture)
            : ((long)Math.Round(number)).ToString();
        await _apply(this, value);
    }
}
