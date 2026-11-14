using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoWallRW.Core.Persistence;

namespace VideoWallRW.App.ViewModels;

/// <summary>Aba de cena (T30) — cada cena é uma grade completa e independente;
/// clicar troca a grade inteira da tela do operador pra a dela (via MainViewModel.
/// SelectScene, que também sincroniza o engine).</summary>
public sealed partial class SceneViewModel : ObservableObject
{
    private readonly Action<SceneViewModel> _select;
    private readonly Action<SceneViewModel> _remove;

    /// <summary>Avisa que a cena foi renomeada / ganhou atalho — MainViewModel usa pra
    /// marcar o projeto como não salvo. Sem isto, renomear uma cena gravava no modelo
    /// mas o título não ganhava o "*", e fechar o programa levava o nome embora sem
    /// nem perguntar.</summary>
    public Action? Edited { get; set; }

    public SceneSnapshot Model { get; }
    public Guid Id => Model.Id;

    [ObservableProperty] private string _name;
    [ObservableProperty] private string? _hotkey;

    /// <summary>Destaque visual da aba atualmente aberta — controlado por MainViewModel.SelectScene.</summary>
    [ObservableProperty] private bool _isSelected;

    public SceneViewModel(SceneSnapshot model, Action<SceneViewModel> select, Action<SceneViewModel> remove)
    {
        Model = model;
        _select = select;
        _remove = remove;
        _name = model.Name;
        _hotkey = model.Hotkey;
    }

    [RelayCommand]
    private void Trigger() => _select(this);

    [RelayCommand]
    private void Remove() => _remove(this);

    [RelayCommand]
    private void Rename()
    {
        string? newName = TextPromptWindow.Ask("Renomear cena", Name);
        if (newName is not null)
            Name = newName;
    }

    partial void OnNameChanged(string value)
    {
        Model.Name = value;
        Edited?.Invoke();
    }

    partial void OnHotkeyChanged(string? value)
    {
        Model.Hotkey = value;
        Edited?.Invoke();
    }
}
