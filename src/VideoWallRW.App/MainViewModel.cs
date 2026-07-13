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

namespace VideoWallRW.App;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly GraphicsDevice _gfx;
    private readonly RenderThread _render;

    public ObservableCollection<MonitorViewModel> Monitors { get; } = [];

    [ObservableProperty] private string _statusText = "Pronto — marque um monitor para ativar a saída.";

    // ── Clip ──────────────────────────────────────────────────────────────────
    [ObservableProperty] private bool _clipLoaded;
    [ObservableProperty] private string _playPauseLabel = "▶ Play";
    [ObservableProperty] private string _timecodeText = "--:--.- / --:--.-";
    [ObservableProperty] private double _seekPosition;     // 0.0 .. 1.0
    [ObservableProperty] private double _seekMax = 1.0;



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

    [RelayCommand]
    private void OpenVideo()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Abrir vídeo",
            Filter = "Vídeos|*.mp4;*.mov;*.mkv;*.avi;*.wmv;*.webm;*.ts;*.m4v|Todos os arquivos|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        _render.Post(new LoadTestClip(dlg.FileName));
        ClipLoaded = true;
        PlayPauseLabel = "⏸ Pause";
        StatusText = $"Carregando: {Path.GetFileName(dlg.FileName)}";
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (!ClipLoaded) return;
        if (PlayPauseLabel.StartsWith("▶"))
        {
            _render.Post(new PlayClip());
            PlayPauseLabel = "⏸ Pause";
        }
        else
        {
            _render.Post(new PauseClip());
            PlayPauseLabel = "▶ Play";
        }
    }

    [RelayCommand]
    private void SeekTo(double normalizedPos)
    {
        if (!ClipLoaded || SeekMax <= 0) return;
        _render.Post(new SeekClip(normalizedPos * SeekMax));
    }

    [RelayCommand]
    private void SetSpeed(string? speedStr)
    {
        if (!ClipLoaded) return;
        if (double.TryParse(speedStr, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double speed))
            _render.Post(new SetClipSpeed(speed));
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
            else if (!ClipLoaded)
                StatusText = "Nenhuma saída ativa";

            if (stats.Clip is { } clip)
            {
                // Timecode
                TimecodeText = $"{FormatTime(clip.PlayheadSeconds)} / {FormatTime(clip.DurationSeconds)}";

                // Slider de seek (sem causar loop de feedback — seek é disparado apenas no MouseUp).
                if (clip.DurationSeconds > 0)
                {
                    SeekMax = clip.DurationSeconds;
                    SeekPosition = clip.PlayheadSeconds;
                }

                // Label play/pause sincronizado com o estado real.
                PlayPauseLabel = clip.State == PlaybackState.Playing ? "⏸ Pause" : "▶ Play";
            }
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
