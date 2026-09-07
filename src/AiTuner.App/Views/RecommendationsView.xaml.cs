using System.Windows.Controls;
using AiTuner.App.ViewModels;

namespace AiTuner.App.Views;

public partial class RecommendationsView : UserControl
{
    public RecommendationsView()
    {
        InitializeComponent();
        DataContext = MainViewModel.Instance;
    }
}
