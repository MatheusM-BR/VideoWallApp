using System.Windows;
using System.Windows.Controls;

namespace VideoWallRW.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        DataContext = vm;
        _vm = vm;
        InitializeComponent();
    }

    // O slider de seek dispara SeekTo no MouseUp para evitar seek contínuo enquanto
    // o usuário arrasta (que degeneraria em centenas de comandos por segundo).
    private void OnSeekMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is Slider slider)
            _vm.SeekToCommand.Execute(slider.Value / Math.Max(slider.Maximum, 0.001));
    }

    private void OnSpeedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo && combo.SelectedItem is ComboBoxItem item)
            _vm.SetSpeedCommand.Execute(item.Tag?.ToString());
    }
}
