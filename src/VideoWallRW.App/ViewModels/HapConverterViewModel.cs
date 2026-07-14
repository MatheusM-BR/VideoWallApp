using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using VideoWallRW.Media.Hap;

namespace VideoWallRW.App.ViewModels;

public sealed partial class HapConverterViewModel : ObservableObject
{
    private readonly string? _ffmpegPath;

    public ObservableCollection<ConvertFileViewModel> Files { get; } = [];
    public IReadOnlyList<HapConverterPreset> Presets { get; } = Enum.GetValues<HapConverterPreset>();

    [ObservableProperty] private HapConverterPreset _selectedPreset = HapConverterPreset.Hap1;
    [ObservableProperty] private string _destinationFolder = "";
    [ObservableProperty] private bool _isConverting;
    [ObservableProperty] private string _statusText;

    public HapConverterViewModel()
    {
        _ffmpegPath = HapConverterService.FindFfmpegExecutable();
        StatusText = _ffmpegPath is null
            ? "ffmpeg.exe não encontrado (third_party\\ffmpeg-cli\\ffmpeg.exe ou no PATH) — conversão indisponível."
            : $"ffmpeg encontrado: {_ffmpegPath}";
    }

    [RelayCommand]
    private void AddFiles()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Selecionar vídeos para converter",
            Multiselect = true,
            Filter = "Vídeos|*.mp4;*.mov;*.mkv;*.avi;*.webm|Todos os arquivos|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        foreach (var path in dlg.FileNames)
            Files.Add(new ConvertFileViewModel(path));

        if (string.IsNullOrEmpty(DestinationFolder) && dlg.FileNames.Length > 0)
            DestinationFolder = Path.GetDirectoryName(dlg.FileNames[0]) ?? "";
    }

    [RelayCommand]
    private void RemoveFile(ConvertFileViewModel? file)
    {
        if (file is not null)
            Files.Remove(file);
    }

    [RelayCommand]
    private void BrowseDestination()
    {
        var dlg = new OpenFolderDialog { Title = "Pasta de destino" };
        if (dlg.ShowDialog() == true)
            DestinationFolder = dlg.FolderName;
    }

    [RelayCommand]
    private async Task ConvertAllAsync()
    {
        if (_ffmpegPath is null || Files.Count == 0 || IsConverting)
            return;

        IsConverting = true;
        try
        {
            foreach (var file in Files)
            {
                if (file.Status == "OK")
                    continue;

                file.Status = "Convertendo...";
                string outputPath = Path.Combine(DestinationFolder,
                    Path.GetFileNameWithoutExtension(file.Path) + "_hap.mov");
                try
                {
                    var progress = new Progress<double>(p => file.Progress = p);
                    await HapConverterService.ConvertAsync(_ffmpegPath, file.Path, outputPath, SelectedPreset, progress, CancellationToken.None);
                    file.Status = "OK";
                    file.OutputPath = outputPath;
                }
                catch (Exception ex)
                {
                    file.Status = $"Erro: {ex.Message}";
                }
            }
            StatusText = "Conversão concluída.";
        }
        finally
        {
            IsConverting = false;
        }
    }
}

public sealed partial class ConvertFileViewModel(string path) : ObservableObject
{
    public string Path { get; } = path;
    public string FileName => System.IO.Path.GetFileName(Path);

    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _status = "Pendente";
    [ObservableProperty] private string? _outputPath;
}
