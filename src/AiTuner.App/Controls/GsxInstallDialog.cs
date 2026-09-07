using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using AiTuner.Core.Libraries;

namespace AiTuner.App.Controls;

/// <summary>Variantenwahl für GSX-Profile (VDGS-Fassungen) — im Look der anderen Dialoge.</summary>
public static class GsxInstallDialog
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    /// <summary>Liefert je Airport die gewählte Variante (oder null = überspringen); null gesamt = Abbruch.</summary>
    public static List<(GsxProfileCandidate Candidate, GsxProfileVariant? Variant)>? Show(
        IReadOnlyList<GsxProfileCandidate> candidates, bool hasNool, bool hasAerosoftVdgs)
    {
        List<(GsxProfileCandidate, GsxProfileVariant?)>? result = null;

        var window = new Window
        {
            Title = "GSX-Profile installieren",
            Width = 560,
            Height = 400,
            MinHeight = 0,
            MinWidth = 0,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            Background = Brushes.Transparent,
        };
        var owner = Application.Current?.MainWindow;
        if (owner != null && !ReferenceEquals(owner, window))
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            int pref = 2;
            _ = DwmSetWindowAttribute(hwnd, 33, ref pref, sizeof(int));
        };

        Brush Res(string key, string fallback) =>
            Application.Current?.TryFindResource(key) as Brush
            ?? new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback));
        var textPrimary = Res("BrushTextPrimary", "#FFF0F0F0");
        var textSecondary = Res("BrushTextSecondary", "#FFB8BCC2");
        var textTertiary = Res("BrushTextTertiary", "#FF8A8F98");
        var accent = Res("BrushAccent", "#FF4CC2FF");

        var titleText = new TextBlock
        {
            Text = window.Title, FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = textPrimary, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(20, 0, 0, 0),
        };
        var closeButton = new Button
        {
            Content = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 10, Width = 40, Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Foreground = textSecondary,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        closeButton.Click += (_, _) => window.Close();
        var titleRow = new Grid { Height = 42, Background = Brushes.Transparent };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(closeButton, 1);
        titleRow.Children.Add(titleText);
        titleRow.Children.Add(closeButton);
        titleRow.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) window.DragMove();
        };

        var intro = new TextBlock
        {
            Text = "Mehrere Fassungen unterscheiden sich meist nur beim VDGS (Andock-Anzeige). Die Vorauswahl passt zu deinen installierten VDGS-Add-ons.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = textTertiary,
            FontSize = 11,
        };

        var sections = new StackPanel();
        var selections = new List<(GsxProfileCandidate Candidate, Func<GsxProfileVariant?> GetChoice)>();
        foreach (var candidate in candidates)
        {
            var header = new TextBlock
            {
                Text = candidate.Variants.Count == 1
                    ? candidate.Icao
                    : $"{candidate.Icao} — {candidate.Variants.Count} Varianten",
                FontWeight = FontWeights.SemiBold,
                Foreground = accent,
                Margin = new Thickness(0, 12, 0, 4),
            };
            sections.Children.Add(header);

            var suggested = GsxProfileService.SuggestVariant(candidate, hasNool, hasAerosoftVdgs);
            var radios = new List<(RadioButton Radio, GsxProfileVariant? Variant)>();
            var groupName = "gsx_" + candidate.Icao;
            foreach (var variant in candidate.Variants)
            {
                var line = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
                line.Inlines.Add(new System.Windows.Documents.Run(variant.Vdgs) { Foreground = textPrimary });
                line.Inlines.Add(new System.Windows.Documents.Run($"   {variant.Label}") { Foreground = textTertiary, FontSize = 11 });
                var radio = new RadioButton
                {
                    GroupName = groupName,
                    Content = line,
                    IsChecked = ReferenceEquals(variant, suggested),
                    Margin = new Thickness(4, 2, 0, 2),
                    ToolTip = string.Join("\n", variant.Files.Select(System.IO.Path.GetFileName)),
                };
                radios.Add((radio, variant));
                sections.Children.Add(radio);
            }
            if (candidate.Variants.Count > 1 || candidates.Count > 1)
            {
                var skip = new RadioButton
                {
                    GroupName = groupName,
                    Content = new TextBlock { Text = "Nicht installieren", Foreground = textTertiary },
                    Margin = new Thickness(4, 2, 0, 2),
                };
                radios.Add((skip, null));
                sections.Children.Add(skip);
            }
            selections.Add((candidate, () => radios.FirstOrDefault(r => r.Radio.IsChecked == true).Variant));
        }

        var scroll = new ScrollViewer
        {
            Content = sections,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 380,
        };

        var okButton = new Button { Content = "Installieren", MinWidth = 110, IsDefault = true };
        okButton.Click += (_, _) =>
        {
            result = selections.Select(s => (s.Candidate, s.GetChoice())).ToList();
            window.DialogResult = true;
        };
        var cancelButton = new Button { Content = "Abbrechen", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };
        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);

        var panel = new StackPanel { Margin = new Thickness(20, 2, 20, 20) };
        panel.Children.Add(intro);
        panel.Children.Add(scroll);
        panel.Children.Add(buttons);

        var root = new StackPanel();
        root.Children.Add(titleRow);
        root.Children.Add(panel);
        window.Content = new Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF20232A")),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF3A3F47")),
            BorderThickness = new Thickness(1),
            Child = root,
        };

        window.Loaded += (_, _) =>
        {
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(window.Width, double.PositiveInfinity));
            var desired = content.DesiredSize.Height;
            if (desired > 100)
            {
                window.Top += (window.ActualHeight - desired) / 2;
                window.Height = desired;
            }
        };

        return window.ShowDialog() == true ? result : null;
    }
}
