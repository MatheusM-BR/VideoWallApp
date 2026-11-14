using System.Windows;
using System.Windows.Threading;
using VideoWallRW.App.ViewModels;

namespace VideoWallRW.App;

public partial class OutputSetupWindow : Window
{
    private readonly DispatcherTimer _previewTimer;

    public OutputSetupWindow(OutputSetupViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // PreviewImage não notifica sozinho (getter computada a partir de um Func
        // externo) — sem isso, ligar o Composition Monitor com este diálogo já aberto
        // nunca troca a imagem de teste pela captura ao vivo.
        _previewTimer = new DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(500) };
        _previewTimer.Tick += (_, _) => viewModel.RefreshPreviewImage();
        _previewTimer.Start();
        Closed += (_, _) => _previewTimer.Stop();
    }
}
