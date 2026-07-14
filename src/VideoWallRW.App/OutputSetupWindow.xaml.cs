using System.Windows;
using VideoWallRW.App.ViewModels;

namespace VideoWallRW.App;

public partial class OutputSetupWindow : Window
{
    public OutputSetupWindow(OutputSetupViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
