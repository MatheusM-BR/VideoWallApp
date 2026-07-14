using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
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

namespace VideoWallRW.App;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly GraphicsDevice _gfx;
    private readonly RenderThread _render;
    private bool _masterPaused;
    private bool _blackout;
    private double _opacityBeforeBlackout = 1.0;

    public ObservableCollection<MonitorViewModel> Monitors { get; } = [];

    [ObservableProperty] private string _statusText = "Pronto — marque um monitor para ativar a saída.";

    public CompositionViewModel Grid { get; }    public MainViewModel(GraphicsDevice gfx, RenderThread render)
    {
        _gfx = gfx;
        _render = render;
        
        var compModel = new Composition { ColumnCount = 8 };
        compModel.AddLayer(new Layer { Name = "Layer 3" });
        compModel.AddLayer(new Layer { Name = "Layer 2" });
        compModel.AddLayer(new Layer { Name = "Layer 1" }); // Base
        
        Grid = new CompositionViewModel(compModel, _render);
        Grid.SyncToEngine(); // Manda a árvore pro Renderer Thread
        
        _render.StatsUpdated += OnStatsUpdated;
        _render.EngineFailed += OnEngineFailed;
        RefreshMonitors();
    }

    [RelayCommand]
    private void RefreshMonitors()
    {
        var previouslyActive = Monitors.Where(m => m.IsActive)
            .Select(m => m.Info.DeviceName).ToHashSet();
        Monitors.Clear();
        foreach (var info in MonitorEnumerator.Enumerate(_gfx))
            Monitors.Add(new MonitorViewModel(info, _render)
            {
                IsActive = previouslyActive.Contains(info.DeviceName),
            });
    }

    [RelayCommand]
    private void OpenOutputSetup()
    {
        var vm = new OutputSetupViewModel(_render, MonitorEnumerator.Enumerate(_gfx),
            () => Grid.Model.Width, () => Grid.Model.Height);
        var window = new OutputSetupWindow(vm) { Owner = Application.Current.MainWindow };
        window.ShowDialog();
    }

    [RelayCommand]
    private void OpenHapConverter()
    {
        var window = new HapConverterWindow(new HapConverterViewModel()) { Owner = Application.Current.MainWindow };
        window.ShowDialog();
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
        
        // P/ Teste: coloca na Layer 0 (superior no resolume index), Col 0
        var ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
        SourceDescriptor src = ext is ".png" or ".jpg" or ".jpeg" 
            ? new FileImageSource(dlg.FileName) 
            : new FileVideoSource(dlg.FileName);

        Grid.SetClip(0, 0, new Clip(src) { Name = Path.GetFileName(dlg.FileName) });
        
        StatusText = $"Load p/ testes em [0,0]: {Path.GetFileName(dlg.FileName)}";
    }

    private void OnStatsUpdated(EngineStats stats)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            foreach (var monitor in Monitors)
            {
                var output = stats.Outputs.FirstOrDefault(
                    o => o.MonitorDeviceName == monitor.Info.DeviceName);
                monitor.FpsText = output is null ? "" : $"{output.Fps:F1} fps";
            }

            if (stats.Outputs.Count > 0)
                StatusText = string.Join("   |   ",
                    stats.Outputs.Select(o => $"{o.MonitorDeviceName}: {o.Fps:F1} fps"));
            else
                StatusText = "Nenhuma saída ativa";

            // (Opcional) Poderíamos monitorar o stats.Clip para o painel de propriedades...
        });
    }

    private void OnEngineFailed(string message)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            StatusText = $"ERRO no engine: {message}";
            foreach (var monitor in Monitors) monitor.IsActive = false;
        });
    }

    private static string FormatTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds / 100}";
    }

    public void ToggleMasterPause()
    {
        _masterPaused = !_masterPaused;
        _render.Post(new SetMasterPaused(_masterPaused));
        StatusText = _masterPaused ? "Pausa global ativa" : "Pausa global desativada";
    }

    public void ToggleBlackout()
    {
        if (!_blackout)
        {
            _opacityBeforeBlackout = Grid.Model.MasterOpacity;
            Grid.Model.MasterOpacity = 0;
        }
        else
        {
            Grid.Model.MasterOpacity = _opacityBeforeBlackout;
        }

        _blackout = !_blackout;
        _render.Post(new SetMasterOpacity(Grid.Model.MasterOpacity));
        StatusText = _blackout ? "Blackout ativo" : "Blackout desativado";
    }

    public void ToggleOutputs()
    {
        bool enableOutputs = Monitors.All(monitor => !monitor.IsActive);
        foreach (var monitor in Monitors)
            monitor.IsActive = enableOutputs;
    }
}

public sealed partial class MonitorViewModel : ObservableObject
{
    private readonly RenderThread _render;

    public MonitorInfo Info { get; }
    public string Title => $"{Info.DeviceName} — {Info.Width}×{Info.Height} @ ({Info.X},{Info.Y})";

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private string _fpsText = "";

    public MonitorViewModel(MonitorInfo info, RenderThread render)
    {
        Info = info;
        _render = render;
    }

    partial void OnIsActiveChanged(bool value)
        => _render.Post(value
            ? new EnableOutput(Info.DeviceName)
            : new DisableOutput(Info.DeviceName));
}
