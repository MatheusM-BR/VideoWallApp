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

public sealed partial class MonitorViewModel : ObservableObject
{
    private readonly RenderThread _render;
    private readonly Action<MonitorViewModel, int?>? _routeChanged;
    private bool _suppressRoute;

    public MonitorInfo Info { get; }

    /// <summary>Nome dado pelo operador no Advanced Output (Screen.Name) — quando
    /// definido, o rodapé SAÍDAS mostra ele em vez do nome técnico do Windows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private string? _customName;

    public string Title
    {
        get
        {
            string name = string.IsNullOrWhiteSpace(CustomName) || CustomName == Info.DeviceName
                ? Info.DeviceName
                : $"{CustomName} ({Info.DeviceName})";
            return $"{name} — {Info.Width}×{Info.Height} @ ({Info.X},{Info.Y})";
        }
    }

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private string _fpsText = "";

    /// <summary>Opções de roteamento: [0]="Composição", [1..]="layers".</summary>
    public ObservableCollection<string> RouteOptions { get; } = [];

    /// <summary>0 = composição inteira; N = layer N-1 isolada neste monitor.</summary>
    [ObservableProperty] private int _selectedRouteIndex;

    /// <summary>Avisa que o telão foi ligado/desligado — o projeto guarda quais
    /// monitores estavam LIGADOS (ProjectMeta.EnabledOutputs) pra religar sozinho na
    /// próxima abertura, então marcar/desmarcar tem que sujar o projeto.</summary>
    public Action? Edited { get; set; }

    public MonitorViewModel(MonitorInfo info, RenderThread render,
        Action<MonitorViewModel, int?>? routeChanged = null)
    {
        Info = info;
        _render = render;
        _routeChanged = routeChanged;
    }

    public void SetRouteOptions(IReadOnlyList<string> layerNames, int? currentLayerIndex)
    {
        _suppressRoute = true;
        RouteOptions.Clear();
        RouteOptions.Add("Composição");
        for (int i = 0; i < layerNames.Count; i++)
            RouteOptions.Add($"{i + 1}: {layerNames[i]}");
        SelectedRouteIndex = currentLayerIndex is { } idx && idx >= 0 && idx < layerNames.Count ? idx + 1 : 0;
        _suppressRoute = false;
    }

    partial void OnSelectedRouteIndexChanged(int value)
    {
        if (_suppressRoute) return;
        _routeChanged?.Invoke(this, value <= 0 ? null : value - 1);
    }

    partial void OnIsActiveChanged(bool value)
    {
        _render.Post(value
            ? new EnableOutput(Info.DeviceName)
            : new DisableOutput(Info.DeviceName));
        Edited?.Invoke();
    }
}
