using System.Windows.Controls;
using AiTuner.App.ViewModels;

namespace AiTuner.App.Views;

public partial class WindowsView : UserControl
{
    public WindowsView()
    {
        InitializeComponent();
        DataContext = MainViewModel.Instance;
    }
}
