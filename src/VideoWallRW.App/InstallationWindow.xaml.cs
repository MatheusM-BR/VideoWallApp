using System.Windows;
using VideoWallRW.App.ViewModels;

namespace VideoWallRW.App;

public partial class InstallationWindow : Window
{
    public InstallationWindow(InstallationViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
