using System.Windows;
using VideoWallRW.App.ViewModels;

namespace VideoWallRW.App;

public partial class HapConverterWindow : Window
{
    public HapConverterWindow(HapConverterViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
