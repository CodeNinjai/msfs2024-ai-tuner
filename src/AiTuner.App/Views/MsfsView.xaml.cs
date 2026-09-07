using System.Windows.Controls;
using AiTuner.App.ViewModels;

namespace AiTuner.App.Views;

public partial class MsfsView : UserControl
{
    public MsfsView()
    {
        InitializeComponent();
        DataContext = MainViewModel.Instance;
    }
}
