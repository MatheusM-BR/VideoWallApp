using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoWallRW.App.Input;

namespace VideoWallRW.App.ViewModels;

/// <summary>Editor de atalhos (Fase 7 T7.7) — clicar "Redefinir" e apertar a nova tecla.</summary>
public sealed partial class KeymapEditorViewModel : ObservableObject
{
    public ObservableCollection<KeymapEntryViewModel> Entries { get; } = [];

    [ObservableProperty] private string _statusText = "";

    private readonly IReadOnlyList<LayerTargetOption> _layerOptions;

    public KeymapEditorViewModel(IEnumerable<string> layerNames)
    {
        _layerOptions = [new LayerTargetOption(null, "Todas as camadas"),
            .. layerNames.Select((name, index) => new LayerTargetOption(index, $"Camada {index + 1} · {name}"))];
        LoadEntries(KeymapService.LoadOrCreate(KeymapService.FilePath));
    }

    private void LoadEntries(Dictionary<string, string> map)
    {
        Entries.Clear();
        var targets = KeymapService.LoadLayerTargets();
        foreach (var (action, gesture) in map.OrderBy(kv => kv.Key))
            Entries.Add(new KeymapEntryViewModel(action, gesture, _layerOptions,
                targets.GetValueOrDefault(action)));
        CheckConflicts();
    }

    [RelayCommand]
    private void ResetToDefault() => LoadEntries(KeymapService.Defaults());

    [RelayCommand]
    private void Save()
    {
        var map = Entries.ToDictionary(e => e.Action, e => e.Gesture);
        KeymapService.Save(KeymapService.FilePath, map);
        KeymapService.SaveLayerTargets(Entries.Where(e => e.IsColumnAction)
            .ToDictionary(e => e.Action, e => e.SelectedLayer?.LayerIndex));
        StatusText = "Atalhos salvos.";
    }

    public void ApplyCapturedGesture(string gestureText)
    {
        var capturing = Entries.FirstOrDefault(e => e.IsCapturing);
        if (capturing is null) return;
        capturing.Gesture = gestureText;
        capturing.IsCapturing = false;
        CheckConflicts();
    }

    public void CheckConflicts()
    {
        foreach (var e in Entries) e.HasConflict = false;
        foreach (var group in Entries.Where(e => !string.IsNullOrWhiteSpace(e.Gesture)).GroupBy(e => e.Gesture))
        {
            if (group.Count() <= 1) continue;
            foreach (var e in group) e.HasConflict = true;
        }
    }
}

public sealed partial class KeymapEntryViewModel : ObservableObject
{
    public string Action { get; }

    [ObservableProperty] private string _gesture;
    [ObservableProperty] private bool _hasConflict;
    [ObservableProperty] private bool _isCapturing;
    [ObservableProperty] private LayerTargetOption? _selectedLayer;
    public IReadOnlyList<LayerTargetOption> LayerOptions { get; }
    public bool IsColumnAction => Action.StartsWith("TriggerColumn", StringComparison.Ordinal);

    public KeymapEntryViewModel(string action, string gesture,
        IReadOnlyList<LayerTargetOption> layerOptions, int? selectedLayerIndex)
    {
        Action = action;
        _gesture = gesture;
        LayerOptions = layerOptions;
        _selectedLayer = layerOptions.FirstOrDefault(x => x.LayerIndex == selectedLayerIndex) ?? layerOptions[0];
    }

    [RelayCommand]
    private void StartCapture() => IsCapturing = true;
}

public sealed record LayerTargetOption(int? LayerIndex, string Name);
