using System.Windows.Controls;
using AiTuner.App.ViewModels;

namespace AiTuner.App.Views;

public partial class BenchmarkView : UserControl
{
    public BenchmarkView()
    {
        InitializeComponent();
        DataContext = MainViewModel.Instance;
    }
}
