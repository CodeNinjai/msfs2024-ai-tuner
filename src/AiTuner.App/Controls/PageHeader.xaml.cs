using System.Windows;
using System.Windows.Controls;

namespace AiTuner.App.Controls;

/// <summary>Einheitlicher Seitenkopf: farbige Icon-Kachel + Titel + Untertitel.</summary>
public partial class PageHeader : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(PageHeader), new PropertyMetadata(""));

    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(PageHeader), new PropertyMetadata(""));

    public static readonly DependencyProperty IconKeyProperty =
        DependencyProperty.Register(nameof(IconKey), typeof(string), typeof(PageHeader), new PropertyMetadata(""));

    public static readonly DependencyProperty TileBgProperty =
        DependencyProperty.Register(nameof(TileBg), typeof(string), typeof(PageHeader), new PropertyMetadata("#334CC2FF"));

    public static readonly DependencyProperty TileFgProperty =
        DependencyProperty.Register(nameof(TileFg), typeof(string), typeof(PageHeader), new PropertyMetadata("#FF4CC2FF"));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public string IconKey
    {
        get => (string)GetValue(IconKeyProperty);
        set => SetValue(IconKeyProperty, value);
    }

    public string TileBg
    {
        get => (string)GetValue(TileBgProperty);
        set => SetValue(TileBgProperty, value);
    }

    public string TileFg
    {
        get => (string)GetValue(TileFgProperty);
        set => SetValue(TileFgProperty, value);
    }

    public PageHeader()
    {
        InitializeComponent();
    }
}
