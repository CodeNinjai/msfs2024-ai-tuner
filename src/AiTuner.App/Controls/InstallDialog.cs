using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using AiTuner.Core.Libraries;

namespace AiTuner.App.Controls;

public sealed record InstallChoice(string? TargetLibrary, bool Activate, bool ToCommunity2024 = false);

/// <summary>Zielwahl für die Add-on-Installation (Community oder Bibliothek), im PromptWindow-Look.</summary>
public static class InstallDialog
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public static InstallChoice? Show(IReadOnlyList<InstallCandidate> candidates, IReadOnlyList<(string Path, string Name)> libraries,
        bool hasCommunity2024 = false)
    {
        InstallChoice? result = null;

        var window = new Window
        {
            Title = "Add-on installieren",
            Width = 540,
            Height = 320,
            // Wie PromptWindow: MinHeight/MinWidth des impliziten WPF-UI-Window-Styles neutralisieren.
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
            int pref = 2; // DWMWCP_ROUND
            _ = DwmSetWindowAttribute(hwnd, 33, ref pref, sizeof(int));
        };

        Brush Res(string key, string fallback) =>
            Application.Current?.TryFindResource(key) as Brush
            ?? new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback));
        var textPrimary = Res("BrushTextPrimary", "#FFF0F0F0");
        var textSecondary = Res("BrushTextSecondary", "#FFB8BCC2");
        var textTertiary = Res("BrushTextTertiary", "#FF8A8F98");

        // Titelzeile
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

        // Gefundene Pakete
        var packagesHeader = new TextBlock
        {
            Text = candidates.Count == 1 ? "Gefundenes Paket:" : $"Gefundene Pakete ({candidates.Count}):",
            Foreground = textSecondary,
        };
        var list = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var candidate in candidates.Take(6))
        {
            var line = new TextBlock
            {
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = textPrimary,
                Margin = new Thickness(0, 1, 0, 1),
            };
            line.Inlines.Add(new System.Windows.Documents.Run("• " + candidate.Title));
            var extra = candidate.Version.Length > 0 ? $"  ({candidate.FolderName} · v{candidate.Version})" : $"  ({candidate.FolderName})";
            line.Inlines.Add(new System.Windows.Documents.Run(extra) { Foreground = textTertiary, FontSize = 11 });
            list.Children.Add(line);
        }
        if (candidates.Count > 6)
            list.Children.Add(new TextBlock { Text = $"… und {candidates.Count - 6} weitere", Foreground = textTertiary, FontSize = 11 });

        // Zielwahl
        var targetCombo = new ComboBox { Margin = new Thickness(0, 14, 0, 0) };
        targetCombo.Items.Add(new ComboBoxItem
        {
            Content = hasCommunity2024 ? "Community-Ordner (aktiv — MSFS 2020 + 2024)" : "Community-Ordner (fest installiert, sofort aktiv)",
            Tag = "community",
        });
        if (hasCommunity2024)
            targetCombo.Items.Add(new ComboBoxItem { Content = "Community2024-Ordner (aktiv — nur MSFS 2024)", Tag = "community2024" });
        foreach (var (path, name) in libraries)
            targetCombo.Items.Add(new ComboBoxItem { Content = $"Bibliothek „{name}“ — {path}", Tag = path });
        targetCombo.SelectedIndex = 0;

        var activateCheck = new CheckBox
        {
            Content = "Nach der Installation aktivieren (Link im Community-Ordner)",
            IsChecked = true,
            Margin = new Thickness(0, 10, 0, 0),
            Foreground = textSecondary,
            IsEnabled = false,
        };
        bool IsLibraryTag(object? tag) => tag is string s && s is not ("community" or "community2024");
        targetCombo.SelectionChanged += (_, _) =>
        {
            activateCheck.IsEnabled = IsLibraryTag((targetCombo.SelectedItem as ComboBoxItem)?.Tag);
        };

        var okButton = new Button { Content = "Installieren", MinWidth = 110, IsDefault = true };
        okButton.Click += (_, _) =>
        {
            var tag = (targetCombo.SelectedItem as ComboBoxItem)?.Tag as string;
            result = IsLibraryTag(tag)
                ? new InstallChoice(tag, activateCheck.IsChecked == true)
                : new InstallChoice(null, Activate: true, ToCommunity2024: tag == "community2024");
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
        panel.Children.Add(packagesHeader);
        panel.Children.Add(list);
        panel.Children.Add(targetCombo);
        panel.Children.Add(activateCheck);
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
            // Wie PromptWindow: Hoehe explizit aus dem Inhalt messen (SizeToContent ist bei DPI-Skalierung kaputt).
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
