using System.Windows;
using VideoWallRW.App.ViewModels;

namespace VideoWallRW.App;

public partial class AgendaWindow : Window
{
    public AgendaWindow(AgendaViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Closed += (_, _) => viewModel.Dispose();
    }
}
