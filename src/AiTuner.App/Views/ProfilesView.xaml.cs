using System.Windows.Controls;
using AiTuner.App.ViewModels;

namespace AiTuner.App.Views;

public partial class ProfilesView : UserControl
{
    public ProfilesView()
    {
        InitializeComponent();
        DataContext = MainViewModel.Instance;
    }
}
