using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VideoWallRW.App.Remote;

namespace VideoWallRW.App;

/// <summary>
/// Janela do botão "Controle remoto" do topo: explica o recurso, mostra o endereço desta
/// máquina na rede e o PIN, e liga/desliga o servidor (que começa sempre desligado).
/// </summary>
public partial class RemoteControlWindow : Window
{
    private readonly RemoteWebService? _service;
    private bool _busy;

    public sealed record AddressRow(string Url, string InterfaceName);

    public RemoteControlWindow(RemoteWebService? service)
    {
        _service = service;
        InitializeComponent();
        var addresses = LocalAddresses.Get();
        int port = service?.Port ?? RemoteWebService.DefaultPort;
        AddressList.ItemsSource = addresses.Select(a => new AddressRow($"http://{a.Address}:{port}", a.InterfaceName)).ToList();
        NoAddressText.Visibility = addresses.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_service is not null) _service.StateChanged += OnServiceStateChanged;
        Closed += (_, _) => { if (_service is not null) _service.StateChanged -= OnServiceStateChanged; };
        Refresh();
    }

    private void OnServiceStateChanged() => Dispatcher.BeginInvoke(Refresh);

    private void Refresh()
    {
        bool on = _service?.IsRunning == true;
        int clients = _service?.ConnectedClients ?? 0;

        StatusText.Text = !on ? "DESLIGADO"
            : clients == 0 ? "LIGADO · nenhum aparelho"
            : clients == 1 ? "LIGADO · 1 aparelho conectado"
            : $"LIGADO · {clients} aparelhos conectados";
        StatusPill.Background = on ? (Brush)FindResource("AccentDim") : (Brush)FindResource("CellBg");

        PinText.Text = on ? string.Join(" ", _service!.Pin.ToCharArray()) : "— — — — — —";
        PinHint.Text = on
            ? "Este PIN vale até desligar. Ao ligar de novo, um PIN novo é sorteado."
            : "O PIN aparece quando o controle é ligado — e muda a cada vez que liga.";

        ToggleButton.Content = _busy ? "Aguarde…" : on ? "Desligar controle remoto" : "Ligar controle remoto";
        ToggleButton.IsEnabled = !_busy && _service is not null;

        string? error = _service is null ? "Controle remoto indisponível nesta sessão." : _service.LastError;
        ErrorText.Text = error is null ? "" : $"Não foi possível ligar: {error}";
        ErrorText.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnToggle(object sender, RoutedEventArgs e)
    {
        if (_service is null || _busy) return;
        _busy = true;
        Refresh();
        try
        {
            if (_service.IsRunning) await _service.StopAsync();
            else await _service.StartAsync();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Falha ao alternar o controle remoto");
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    /// <summary>
    /// Só para os prints do manual (UiChecks): mostra a janela no estado "ligado" com um
    /// PIN e endereço de exemplo, sem subir servidor nenhum.
    /// </summary>
    public void ShowPreview(string pin, int clients, string exampleAddress)
    {
        AddressList.ItemsSource = new[] { new AddressRow($"http://{exampleAddress}:{RemoteWebService.DefaultPort}", "Ethernet") };
        NoAddressText.Visibility = Visibility.Collapsed;
        StatusText.Text = clients == 1 ? "LIGADO · 1 aparelho conectado" : $"LIGADO · {clients} aparelhos conectados";
        StatusPill.Background = (Brush)FindResource("AccentDim");
        PinText.Text = string.Join(" ", pin.ToCharArray());
        PinHint.Text = "Este PIN vale até desligar. Ao ligar de novo, um PIN novo é sorteado.";
        ToggleButton.Content = "Desligar controle remoto";
        ToggleButton.IsEnabled = true;
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void OnCopyAddress(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url }) return;
        try
        {
            Clipboard.SetText(url);
            ((Button)sender).Content = "Copiado";
        }
        catch (System.Runtime.InteropServices.ExternalException) { /* área de transferência ocupada: ignora */ }
    }
}
