using System.Windows.Controls;
using AiTuner.App.ViewModels;

namespace AiTuner.App.Views;

public partial class TuningView : UserControl
{
    public TuningView()
    {
        InitializeComponent();
        DataContext = MainViewModel.Instance;
        Loaded += (_, _) => MainViewModel.Instance.StartTuningWatcher();
        Unloaded += (_, _) => MainViewModel.Instance.StopTuningWatcher();
    }
}
