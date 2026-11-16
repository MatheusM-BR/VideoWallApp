using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoWallRW.Core.Commands;
using VideoWallRW.Core.Model;
using VideoWallRW.Engine;

namespace VideoWallRW.App.ViewModels;

public sealed partial class LayerViewModel : ObservableObject
{
    private readonly int _layerIndex;
    private readonly RenderThread _render;
    private readonly Action<ClipViewModel> _selectClip;
    private readonly Action<ClipViewModel> _clipChanged;

    public int LayerIndex => _layerIndex;

    [ObservableProperty] private string _name = "Camada";
    [ObservableProperty] private BlendMode _blend;
    [ObservableProperty] private bool _bypass;
    [ObservableProperty] private bool _solo;
    [ObservableProperty] private double _transitionSeconds;

    /// <summary>
    /// Última coluna disparada nesta layer pela UI — usado para capturar cenas
    /// (T7.5). Reflete apenas triggers vindos do painel (sem MIDI/OSC ainda,
    /// então é uma fonte de verdade confiável por enquanto).
    /// </summary>
    [ObservableProperty] private int _lastTriggeredColumn = -1;

    /// <summary>Autopilot rápido da layer: escolher aqui aplica a TODOS os clips da
    /// layer de uma vez (o operador monta a playlist sem abrir clip por clip).</summary>
    [ObservableProperty] private AutopilotAction _layerAutopilot;

    /// <summary>Autopilot ON/OFF da layer: OFF = o que estiver no ar segura (fim de
    /// clip não avança nada). O toggle global da barra liga/desliga todas as layers.</summary>
    [ObservableProperty] private bool _autopilotEnabled = true;

    private double _opacity = 1.0;
    public double Opacity
    {
        get => _opacity;
        set
        {
            if (SetProperty(ref _opacity, value))
            {
                Model.Opacity = value;
                _render.Post(new SetLayerOpacity(_layerIndex, value));
                Edited?.Invoke();
            }
        }
    }

    public Layer Model { get; }
    public ObservableCollection<ClipViewModel> Clips { get; } = [];

    public LayerViewModel(Layer model, int layerIndex, RenderThread render,
        Action<ClipViewModel> selectClip, Action<ClipViewModel> clipChanged)
    {
        // Compatibilidade com projetos antigos: o toggle podia estar ON enquanto
        // todos os clips estavam em None, combinação que visualmente prometia avanço
        // mas nunca gerava um evento de fim para o autopilot.
        if (model.AutopilotEnabled && model.Clips.Any(c => c is not null) &&
            model.Clips.Where(c => c is not null).All(c => c!.Autopilot == AutopilotAction.None))
        {
            foreach (var clip in model.Clips)
                if (clip is not null)
                    clip.Autopilot = AutopilotAction.PlayNextClip;
        }
        Model = model;
        _layerIndex = layerIndex;
        _render = render;
        _selectClip = selectClip;
        _clipChanged = clipChanged;
        Name = model.Name;
        _opacity = model.Opacity;
        Blend = model.Blend;
        Bypass = model.Bypass;
        Solo = model.Solo;
        TransitionSeconds = model.TransitionSeconds;
        // Reflete o autopilot atual se todos os clips da layer concordam (sem disparar aplicação).
        _layerAutopilot = model.Clips.FirstOrDefault(c => c is not null)?.Autopilot ?? AutopilotAction.PlayNextClip;
        _autopilotEnabled = model.AutopilotEnabled;
        _ignoreColumnTrigger = model.IgnoreColumnTrigger;

        for (int i = 0; i < model.Clips.Length; i++)
        {
            Clips.Add(new ClipViewModel(model.Clips[i], layerIndex, i, render, _selectClip, _clipChanged,
                clip => LastTriggeredColumn = clip.ColumnIndex));
        }
    }

    public void AddClip(ClipViewModel clip) => Clips.Add(clip);

    [RelayCommand]
    private void Clear()
    {
        _render.Post(new ClearLayer(_layerIndex));
        // Consistência: sem isso, capturar uma cena depois de limpar a layer ainda
        // gravava a coluna antiga como ativa.
        LastTriggeredColumn = -1;
    }

    partial void OnLastTriggeredColumnChanged(int value)
    {
        foreach (var clip in Clips)
            clip.IsActive = clip.ColumnIndex == value;
    }

    /// <summary>
    /// Avisa que uma PROPRIEDADE da layer mudou (opacidade, blend, bypass, solo,
    /// nome, autopilot, layer fixa). Diferente do evento estrutural Changed: não
    /// mexe na forma da grade, mas precisa sujar o projeto e entrar na cena aberta —
    /// sem isto, ajustar a layer e fechar o programa perdia o ajuste sem nem
    /// perguntar. Atribuído pela CompositionViewModel.
    /// </summary>
    public Action? Edited { get; set; }

    // Renomear layer pela UI (o engine não usa o nome; basta persistir no modelo).
    partial void OnNameChanged(string value)
    {
        Model.Name = value;
        Edited?.Invoke();
    }

    partial void OnLayerAutopilotChanged(AutopilotAction value)
    {
        foreach (var clip in Clips)
            if (clip.Model is not null)
                clip.Autopilot = value; // dispara ApplyEdits → UpdateClip no engine, clip a clip
    }

    /// <summary>Layer FIXA: fica de fora do disparo/atalho de coluna inteira — pra
    /// linha de entrada ao vivo (câmera NDI) não cair quando o operador troca a coluna.</summary>
    [ObservableProperty] private bool _ignoreColumnTrigger;

    /// <summary>Ressincroniza a composição inteira no engine — atribuído pela
    /// CompositionViewModel. Necessário porque o engine decide pular a layer no
    /// disparo de coluna lendo a própria cópia da composição.</summary>
    public Action? SyncComposition { get; set; }

    partial void OnIgnoreColumnTriggerChanged(bool value)
    {
        Model.IgnoreColumnTrigger = value;
        SyncComposition?.Invoke();
        Edited?.Invoke();
    }

    partial void OnAutopilotEnabledChanged(bool value)
    {
        if (value && Clips.Any(c => c.Model is not null) &&
            Clips.Where(c => c.Model is not null).All(c => c.Autopilot == AutopilotAction.None))
            LayerAutopilot = AutopilotAction.PlayNextClip;
        Model.AutopilotEnabled = value;
        _render.Post(new SetLayerAutopilot(_layerIndex, value));
        Edited?.Invoke();
    }

    partial void OnBlendChanged(BlendMode value) => UpdateLayerState();
    partial void OnBypassChanged(bool value) => UpdateLayerState();
    partial void OnSoloChanged(bool value) => UpdateLayerState();
    partial void OnTransitionSecondsChanged(double value) => UpdateLayerState();

    private void UpdateLayerState()
    {
        Model.Blend = Blend;
        Model.Bypass = Bypass;
        Model.Solo = Solo;
        Model.TransitionSeconds = TransitionSeconds;
        _render.Post(new SetLayerState(_layerIndex, Blend, Bypass, Solo, TransitionSeconds));
        Edited?.Invoke();
    }
}
