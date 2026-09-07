using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace AiTuner.App.Controls;

/// <summary>Kleiner Eingabedialog im App-Look. Bewusst randloses Standard-Window statt FluentWindow:
/// dessen Mica-Chrome berechnet SizeToContent bei DPI-Skalierung falsch (riesiger Leerraum).</summary>
public static class PromptWindow
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public static string? Show(string title, string message, string initialValue)
    {
        string? result = null;

        var window = new Window
        {
            Title = title,
            Width = 480,
            Height = 230, // Startwert; wird nach dem Laden exakt auf den Inhalt gesetzt
            // Der implizite WPF-UI-Window-Style bringt MinHeight/MinWidth mit und klemmt
            // kleine Dialoge sonst auf Mindestgröße fest — lokal neutralisieren.
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

        // Windows-11-Rundungen für das randlose Fenster
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

        // Titelzeile: Titel links, Schließen rechts, per Drag verschiebbar
        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = textPrimary,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(20, 0, 0, 0),
        };
        var closeButton = new Button
        {
            Content = "", // Segoe-Fluent "ChromeClose"
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 10,
            Width = 40,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = textSecondary,
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

        var textBox = new TextBox
        {
            Text = initialValue,
            Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(8, 6, 8, 6),
        };

        var okButton = new Button { Content = "Übernehmen", MinWidth = 110, IsDefault = true };
        okButton.Click += (_, _) =>
        {
            result = textBox.Text;
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

        var messageText = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = textSecondary,
        };

        var panel = new StackPanel { Margin = new Thickness(20, 2, 20, 20) };
        panel.Children.Add(messageText);
        panel.Children.Add(textBox);
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
            // Kein SizeToContent: das rechnet mit WindowStyle=None bei DPI-Skalierung falsch
            // (Fenster wird um den Skalierungsfaktor zu hoch). Stattdessen Inhalt in DIPs messen
            // und die Hoehe explizit setzen — dieser Pfad ist DPI-sauber.
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(window.Width, double.PositiveInfinity));
            var desired = content.DesiredSize.Height;
            if (desired > 80)
            {
                window.Top += (window.ActualHeight - desired) / 2;
                window.Height = desired;
            }
            textBox.SelectAll();
            textBox.Focus();
        };

        return window.ShowDialog() == true ? result : null;
    }
}
