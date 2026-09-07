using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AiTuner.App.Controls;

/// <summary>Meldungs- und Bestätigungsdialoge im App-Look (Ersatz für MessageBox).</summary>
public static class AppDialog
{
    public enum Kind { Info, Warning, Question }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public static void Info(string title, string message)
        => Show(title, message, Kind.Info, null, "OK");

    public static void Warn(string title, string message)
        => Show(title, message, Kind.Warning, null, "OK");

    /// <summary>true = bestätigt (erster Button), false/Abbruch = zweiter Button bzw. Schließen.</summary>
    public static bool Confirm(string title, string message, string yesLabel = "Ja", string noLabel = "Abbrechen",
        Kind kind = Kind.Question)
        => Show(title, message, kind, yesLabel, noLabel) == true;

    private static bool? Show(string title, string message, Kind kind, string? yesLabel, string cancelLabel)
    {
        var window = new Window
        {
            Title = title,
            Width = 470,
            Height = 220,
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
        var ok = Res("BrushOk", "#FF6CCB5F");
        var warn = Res("BrushWarn", "#FFE5B547");
        var accent = Res("BrushAccent", "#FF4CC2FF");

        var titleText = new TextBlock
        {
            Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold,
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

        // Icon-Kachel links: Häkchen (grün), Warndreieck (gelb) oder Fragezeichen (akzent).
        var (tileBrush, iconKey) = kind switch
        {
            Kind.Info => (ok, "IconCheck"),
            Kind.Warning => (warn, "IconAlert"),
            _ => (accent, ""),
        };
        var tile = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(0x33,
                ((SolidColorBrush)tileBrush).Color.R, ((SolidColorBrush)tileBrush).Color.G, ((SolidColorBrush)tileBrush).Color.B)),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 14, 0),
        };
        if (iconKey.Length > 0 && Application.Current?.TryFindResource(iconKey) is Geometry geometry)
        {
            var path = new Path
            {
                Data = geometry, Stroke = tileBrush, StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round, Width = 24, Height = 24, Stretch = Stretch.None,
            };
            tile.Child = new Viewbox { Width = 18, Height = 18, Child = path };
        }
        else
        {
            tile.Child = new TextBlock
            {
                Text = "?", FontSize = 18, FontWeight = FontWeights.Bold, Foreground = tileBrush,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
        }

        var messageText = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = textSecondary,
        };
        var messageScroll = new ScrollViewer
        {
            Content = messageText,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 420,
            VerticalAlignment = VerticalAlignment.Top,
        };

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(messageScroll, 1);
        body.Children.Add(tile);
        body.Children.Add(messageScroll);

        bool? result = null;
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };
        if (yesLabel is not null)
        {
            var yesButton = new Button { Content = yesLabel, MinWidth = 110, IsDefault = true };
            yesButton.Click += (_, _) => { result = true; window.DialogResult = true; };
            buttons.Children.Add(yesButton);
        }
        var cancelButton = new Button
        {
            Content = cancelLabel, MinWidth = 90, IsCancel = true,
            Margin = new Thickness(yesLabel is null ? 0 : 8, 0, 0, 0),
            IsDefault = yesLabel is null,
        };
        buttons.Children.Add(cancelButton);

        var panel = new StackPanel { Margin = new Thickness(20, 6, 20, 20) };
        panel.Children.Add(body);
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
            if (desired > 90)
            {
                window.Top += (window.ActualHeight - desired) / 2;
                window.Height = desired;
            }
        };

        return window.ShowDialog() == true ? result : null;
    }
}
