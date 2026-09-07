using System.Windows;
using AiTuner.App.ViewModels;
using AiTuner.App.Views;
using Wpf.Ui.Controls;

namespace AiTuner.App;

public partial class MainWindow : FluentWindow
{
    private DashboardView? _dashboard;
    private RecommendationsView? _recommendations;
    private MsfsView? _msfs;
    private AddonsView? _addons;
    private NvidiaView? _nvidia;
    private WindowsView? _windows;
    private ProfilesView? _profiles;
    private BenchmarkView? _benchmark;
    private LibrariesView? _libraries;
    private TuningView? _tuning;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = MainViewModel.Instance;
        ContentHost.Content = _dashboard = new DashboardView();
        Loaded += async (_, _) => await MainViewModel.Instance.LoadAsync();
    }

    /// <summary>Programmatische Navigation (z.B. vom Dashboard-Hero aus).</summary>
    public void NavigateTo(string tag)
    {
        if (tag == "recommendations")
            NavRecommendations.IsChecked = true;
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        // Fires during InitializeComponent for the pre-checked item, before ContentHost exists.
        if (ContentHost is null)
            return;

        ContentHost.Content = (sender as FrameworkElement)?.Tag switch
        {
            "recommendations" => _recommendations ??= new RecommendationsView(),
            "msfs" => _msfs ??= new MsfsView(),
            "addons" => _addons ??= new AddonsView(),
            "nvidia" => _nvidia ??= new NvidiaView(),
            "windows" => _windows ??= new WindowsView(),
            "profiles" => _profiles ??= new ProfilesView(),
            "benchmark" => _benchmark ??= new BenchmarkView(),
            "libraries" => _libraries ??= new LibrariesView(),
            "tuning" => _tuning ??= new TuningView(),
            _ => _dashboard ??= new DashboardView(),
        };
    }
}
