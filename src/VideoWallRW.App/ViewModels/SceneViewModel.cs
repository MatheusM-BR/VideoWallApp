using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoWallRW.Core.Commands;
using VideoWallRW.Core.Persistence;
using VideoWallRW.Engine;

namespace VideoWallRW.App.ViewModels;

/// <summary>Botão de cena (Fase 7 T7.5) — dispara todos os layers/opacidades salvos de uma vez.</summary>
public sealed partial class SceneViewModel : ObservableObject
{
    private readonly RenderThread _render;
    private readonly Action<SceneViewModel> _remove;

    public SceneSnapshot Model { get; }
    public Guid Id => Model.Id;

    [ObservableProperty] private string _name;
    [ObservableProperty] private string? _hotkey;

    public SceneViewModel(SceneSnapshot model, RenderThread render, Action<SceneViewModel> remove)
    {
        Model = model;
        _render = render;
        _remove = remove;
        _name = model.Name;
        _hotkey = model.Hotkey;
    }

    [RelayCommand]
    private void Trigger() => _render.Post(new TriggerScene(Model.Id));

    [RelayCommand]
    private void Remove() => _remove(this);

    partial void OnNameChanged(string value) => Model.Name = value;
    partial void OnHotkeyChanged(string? value) => Model.Hotkey = value;
}
