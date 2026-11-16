using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using VideoWallRW.Core.Commands;
using VideoWallRW.Core.Stats;
using VideoWallRW.Engine;
using VideoWallRW.Engine.Graphics;
using VideoWallRW.Engine.Output;
using VideoWallRW.App.ViewModels;
using VideoWallRW.Core.Model;
using VideoWallRW.Core.Persistence;

namespace VideoWallRW.App;

public sealed partial class MainViewModel
{
    // ── Manual e controle remoto (botões do topo) ─────────────────────────────

    private ManualWindow? _manualWindow;

    [RelayCommand]
    private void OpenHelp()
    {
        if (_manualWindow is { IsLoaded: true })
        {
            _manualWindow.Activate();
            return;
        }
        _manualWindow = new ManualWindow { Owner = Application.Current.MainWindow };
        _manualWindow.Closed += (_, _) => _manualWindow = null;
        _manualWindow.Show();
    }

    private Remote.RemoteWebService? _remote;

    /// <summary>Estado do controle remoto pro botão do topo ("Remoto · LIG.").</summary>
    [ObservableProperty] private bool _remoteRunning;
    [ObservableProperty] private string _remoteButtonText = "Controle remoto";

    public void AttachRemote(Remote.RemoteWebService remote)
    {
        _remote = remote;
        remote.StateChanged += () => Application.Current?.Dispatcher.BeginInvoke(UpdateRemoteState);
        UpdateRemoteState();
    }

    private void UpdateRemoteState()
    {
        RemoteRunning = _remote?.IsRunning == true;
        int clients = _remote?.ConnectedClients ?? 0;
        RemoteButtonText = !RemoteRunning ? "Controle remoto"
            : clients > 0 ? $"Remoto · LIG. ({clients})" : "Remoto · LIG.";
    }

    private Input.OscService? _osc;

    /// <summary>Menu Ferramentas → "Entrada OSC (porta 7000)" (marcável). Desligada ao abrir.</summary>
    [ObservableProperty] private bool _oscEnabled;

    public void AttachOsc(Input.OscService osc) => _osc = osc;

    partial void OnOscEnabledChanged(bool value)
    {
        if (_osc is null) return;
        if (!value)
        {
            _osc.Stop();
            StatusText = "Entrada OSC desligada.";
            return;
        }
        if (_osc.Start())
        {
            StatusText = $"Entrada OSC ligada na porta UDP {Input.OscService.DefaultPort} — sem senha: desligue quando não estiver usando.";
        }
        else
        {
            StatusText = $"Não foi possível ligar a entrada OSC: {_osc.LastError}";
            Application.Current?.Dispatcher.BeginInvoke(() => OscEnabled = false);
        }
    }

    [RelayCommand]
    private void OpenRemoteControl()
    {
        var window = new RemoteControlWindow(_remote) { Owner = Application.Current.MainWindow };
        window.ShowDialog();
    }

    [RelayCommand]
    private void OpenAgenda()
    {
        var vm = new ViewModels.AgendaViewModel(OpenImageEditorForFile, OpenPdfImporter, RevealInLibrary);
        _agendaWindow = new AgendaWindow(vm) { Owner = Application.Current.MainWindow };
        _agendaWindow.Closed += (_, _) => _agendaWindow = null;
        _agendaWindow.Show();
    }

    /// <summary>Abre o editor numa imagem nova (Agenda/PDF/avulsa): a inserção na grade
    /// é pelo chip "🖼 ARRASTE p/ célula" do próprio editor — solta na célula que quiser.
    /// Fecha Agenda/importador de PDF por baixo, senão eles ficam por cima atrapalhando
    /// o arraste das guias de recorte/tela do editor.</summary>
    private void OpenImageEditorForFile(string path, string suggestedName)
    {
        CloseSourceWindows();
        double? compAspect = Grid.Model.Height > 0 ? (double)Grid.Model.Width / Grid.Model.Height : null;
        var vm = new ImageEditorViewModel(path, onSaved: null, compAspect);
        vm.FileSaved += RevealInLibrary;
        var window = new ImageEditorWindow(vm) { Owner = Application.Current.MainWindow };
        window.Show();
    }

    /// <summary>Biblioteca → Arquivos, aberta na pasta do arquivo e com ele selecionado.</summary>
    public void RevealInLibrary(string path)
    {
        ShowSourcesTab = false;
        FileBrowser.ShowFile(path);
    }

    private void OpenPdfImporter(string pdfPath)
    {
        var vm = new PdfImportViewModel(pdfPath, (imagePath, name) => OpenImageEditorForFile(imagePath, name));
        _pdfImportWindow = new PdfImportWindow(vm) { Owner = Application.Current.MainWindow };
        _pdfImportWindow.Closed += (_, _) => _pdfImportWindow = null;
        _pdfImportWindow.Show();
    }

    private void CloseSourceWindows()
    {
        _agendaWindow?.Close();
        _pdfImportWindow?.Close();
    }

    /// <summary>Abre o seletor de célula de destino, pré-selecionando a célula atual.
    /// Retorna null se o operador cancelar.</summary>
    private (int Layer, int Column)? PickTargetCell()
    {
        var dlg = new CellPickerWindow(
            Grid.Layers.Select(l => l.Name).ToList(), Grid.Model.ColumnCount,
            Grid.SelectedClip?.LayerIndex ?? 0, Grid.SelectedClip?.ColumnIndex ?? 0)
        {
            Owner = Application.Current.MainWindow,
        };
        return dlg.ShowDialog() == true ? (dlg.SelectedLayer, dlg.SelectedColumn) : null;
    }

    /// <summary>Painel Sources (duplo-clique num recente) — mesma regra do painel Files.</summary>
    [RelayCommand]
    public void SendRecentSource(string path) => SendFileToSelectedCell(path);

    /// <summary>Painel Files (duplo-clique): com célula selecionada vai direto nela;
    /// sem seleção, pergunta o destino.</summary>
    private void SendFileToSelectedCell(string path)
    {
        int layerIndex, columnIndex;
        if (Grid.SelectedClip is { } selected)
        {
            (layerIndex, columnIndex) = (selected.LayerIndex, selected.ColumnIndex);
        }
        else if (PickTargetCell() is { } cell)
        {
            (layerIndex, columnIndex) = cell;
        }
        else return;

        Grid.SetFiles(layerIndex, columnIndex, [path]);
        StatusText = $"Enviado para a grade: {Path.GetFileName(path)}";
    }

    /// <summary>Reabre o editor numa imagem já na grade (menu de contexto da célula) e,
    /// ao salvar, recarrega o clip no motor sem frame preto (comando ReloadClip).</summary>
    public void EditCellImage(int layerIndex, int columnIndex, string path)
    {
        CloseSourceWindows();
        double? compAspect = Grid.Model.Height > 0 ? (double)Grid.Model.Width / Grid.Model.Height : null;
        var vm = new ImageEditorViewModel(path, (editedPath, _) =>
        {
            var previous = Grid.Model.Layers[layerIndex].Clips[columnIndex];
            Grid.SetClip(layerIndex, columnIndex, new Clip(new FileImageSource(editedPath))
            {
                Name = previous?.Name ?? Path.GetFileNameWithoutExtension(editedPath),
                // Editar não reseta o timer/autopilot que o operador já tinha ajustado.
                ImageDuration = previous?.ImageDuration ?? 7,
                Autopilot = previous?.Autopilot ?? AutopilotAction.PlayNextClip,
            });
            _render.Post(new ReloadClip(layerIndex, columnIndex));
        }, compAspect);
        vm.FileSaved += RevealInLibrary;
        var window = new ImageEditorWindow(vm) { Owner = Application.Current.MainWindow };
        window.Show();
    }

    [RelayCommand]
    private void OpenInstallation()
    {
        var vm = new InstallationViewModel(_installation, UpdateInstallation);
        var window = new InstallationWindow(vm) { Owner = Application.Current.MainWindow };
        window.ShowDialog();
    }

    [RelayCommand]
    private void OpenHapConverter()
    {
        var window = new HapConverterWindow(new HapConverterViewModel()) { Owner = Application.Current.MainWindow };
        window.ShowDialog();
    }

    /// <summary>MainWindow escuta para recarregar o KeymapService sem precisar reiniciar o app.</summary>
    public event Action? KeymapChanged;

    private int _keymapColumns = -1;

    /// <summary>Toda coluna do projeto tem atalho (T: "acompanha a quantidade máxima de
    /// colunas do projeto, independente da cena"). Só mexe no arquivo quando o número de
    /// colunas realmente muda — Grid.Changed dispara a cada edição de célula.</summary>
    private void EnsureColumnShortcuts()
    {
        int columns = Grid.Model.ColumnCount;
        if (columns == _keymapColumns) return;
        _keymapColumns = columns;
        Input.KeymapService.EnsureColumns(Input.KeymapService.FilePath, columns);
        KeymapChanged?.Invoke();
    }

    [RelayCommand]
    private void OpenKeymapEditor()
    {
        var names = Grid.Model.Layers.Select((layer, index) =>
            string.IsNullOrWhiteSpace(layer.Name) ? $"Camada {index + 1}" : layer.Name);
        var window = new KeymapEditorWindow(new KeymapEditorViewModel(names)) { Owner = Application.Current.MainWindow };
        window.ShowDialog();
        KeymapChanged?.Invoke();
    }

    [RelayCommand]
    private void OpenVideo()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Adicionar Arquivo ao Deck",
            Filter = "Mídias|*.mp4;*.mov;*.mkv;*.avi;*.wmv;*.webm;*.png;*.jpg;*.jpeg|Todos os arquivos|*.*",
        };
        if (dlg.ShowDialog() != true) return;

        if (PickTargetCell() is not { } cell)
            return;

        Grid.SetFiles(cell.Layer, cell.Column, [dlg.FileName]);
        StatusText = $"Carregado em [{cell.Layer},{cell.Column}]: {Path.GetFileName(dlg.FileName)}";
    }

}
