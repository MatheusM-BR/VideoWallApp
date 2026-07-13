using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoWallRW.Core.Commands;
using VideoWallRW.Core.Stats;
using VideoWallRW.Engine;
using VideoWallRW.Engine.Graphics;
using VideoWallRW.Engine.Output;

namespace VideoWallRW.App;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly GraphicsDevice _gfx;
    private readonly RenderThread _render;

    public ObservableCollection<MonitorViewModel> Monitors { get; } = [];

    [ObservableProperty]
    private string _statusText = "Pronto — marque um monitor para ativar a saída.";

    public MainViewModel(GraphicsDevice gfx, RenderThread render)
    {
        _gfx = gfx;
        _render = render;
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
            StatusText = stats.Outputs.Count == 0
                ? "Nenhuma saída ativa"
                : string.Join("   |   ",
                    stats.Outputs.Select(o => $"{o.MonitorDeviceName}: {o.Fps:F1} fps"));
        });
    }

    private void OnEngineFailed(string message)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            StatusText = $"ERRO no engine: {message}";
            foreach (var monitor in Monitors)
                monitor.IsActive = false;
        });
    }
}

public sealed partial class MonitorViewModel : ObservableObject
{
    private readonly RenderThread _render;

    public MonitorInfo Info { get; }
    public string Title => $"{Info.DeviceName} — {Info.Width}×{Info.Height} @ ({Info.X},{Info.Y})";

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private string _fpsText = "";

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
