using System.Windows.Controls;
using AiTuner.App.ViewModels;

namespace AiTuner.App.Views;

public partial class NvidiaView : UserControl
{
    public NvidiaView()
    {
        InitializeComponent();
        DataContext = MainViewModel.Instance;
    }
}
