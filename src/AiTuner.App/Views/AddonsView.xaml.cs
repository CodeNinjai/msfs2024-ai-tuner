using System.Windows.Controls;
using AiTuner.App.ViewModels;

namespace AiTuner.App.Views;

public partial class AddonsView : UserControl
{
    public AddonsView()
    {
        InitializeComponent();
        DataContext = MainViewModel.Instance;
    }
}
