using System.Collections.ObjectModel;
using BitmapSource = System.Windows.Media.Imaging.BitmapSource;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoWallRW.App.Controls;
using VideoWallRW.Core.Commands;
using VideoWallRW.Core.Math;
using VideoWallRW.Core.Model;
using VideoWallRW.Core.Persistence;
using VideoWallRW.Engine;
using VideoWallRW.Engine.Output;

namespace VideoWallRW.App.ViewModels;

/// <summary>
/// Editor de Advanced Output (Fase 4): telas + slices com campos numéricos (recorte,
/// posição, rotação, flip, cor). Cada edição reconstrói o OutputSetup e o envia ao
/// engine na hora — "ver o telão reagir enquanto arrasta/edita" também vale para os
/// campos numéricos.
/// </summary>
public sealed partial class OutputSetupViewModel : ObservableObject
{
    private readonly RenderThread _render;
    private readonly Func<int> _compWidth;
    private readonly Func<int> _compHeight;
    private readonly Func<BitmapSource?>? _livePreview;
    private BitmapSource? _testPattern;
    private bool _suppressPush;

    public ObservableCollection<ScreenViewModel> Screens { get; } = [];
    public IReadOnlyList<MonitorInfo> AvailableMonitors { get; }
    public IReadOnlyList<Rotation> Rotations { get; } = Enum.GetValues<Rotation>();

    /// <summary>Tela selecionada — fonte única pra lista, painel de detalhes e o mapa
    /// de telas (T27), assim clicar num retângulo no mapa seleciona na lista e vice-versa.</summary>
    [ObservableProperty] private ScreenViewModel? _selectedScreen;

    /// <summary>Tamanho da composição — palco do editor visual de "Input" de cada slice.</summary>
    public double CompWidth => _compWidth();
    public double CompHeight => _compHeight();
    public string CompResolutionText => $"Composição: {CompWidth:F0}×{CompHeight:F0} ({FormatAspect(CompWidth, CompHeight)})";

    /// <summary>Imagem de referência pros editores de recorte/posição (T28): usa a
    /// captura ao vivo da composição quando disponível (Composition Monitor ligado);
    /// senão cai numa imagem de teste (barras + grade), gerada uma vez por tamanho e
    /// reaproveitada — assim o operador sempre tem algo pra guiar o enquadramento,
    /// mesmo sem pagar o custo de GPU do readback ao vivo.</summary>
    public BitmapSource PreviewImage
    {
        get
        {
            if (_livePreview?.Invoke() is { } live)
                return live;
            if (_testPattern is null || _testPattern.PixelWidth != (int)CompWidth || _testPattern.PixelHeight != (int)CompHeight)
                _testPattern = TestPatternGenerator.Create((int)CompWidth, (int)CompHeight);
            return _testPattern;
        }
    }

    /// <summary>PreviewImage não é [ObservableProperty] (depende de um Func externo,
    /// não de um campo próprio) — sem alguém chamando isto, a captura ao vivo nunca
    /// aparece se o Composition Monitor for ligado só DEPOIS que este diálogo já
    /// estava aberto (o binding WPF só reavalia a getter quando notificado). A janela
    /// chama isto periodicamente enquanto está aberta.</summary>
    public void RefreshPreviewImage() => OnPropertyChanged(nameof(PreviewImage));

    /// <summary>Simplifica W:H pro maior divisor comum (1920x1080 → 16:9), só pra
    /// exibição — ajuda o operador a confirmar de relance se a tela é paisagem ou
    /// retrato sem fazer conta de cabeça.</summary>
    public static string FormatAspect(double width, double height)
    {
        int w = (int)System.Math.Round(width), h = (int)System.Math.Round(height);
        if (w <= 0 || h <= 0) return "?";
        int a = w, b = h;
        while (b != 0) (a, b) = (b, a % b);
        int gcd = System.Math.Max(a, 1);
        return $"{w / gcd}:{h / gcd}";
    }

    [ObservableProperty] private bool _identify;
    [ObservableProperty] private string _presetName = "default";
    [ObservableProperty] private string _statusText = "";

    public ObservableCollection<string> PresetNames { get; } = [];

    public OutputSetupViewModel(RenderThread render, IReadOnlyList<MonitorInfo> monitors,
        Func<int> compWidth, Func<int> compHeight, Func<BitmapSource?>? livePreview = null)
    {
        _render = render;
        AvailableMonitors = monitors;
        _compWidth = compWidth;
        _compHeight = compHeight;
        _livePreview = livePreview;
        RefreshPresetNames();
    }

    [RelayCommand]
    private void AddScreen(MonitorInfo? monitor)
    {
        monitor ??= AvailableMonitors.FirstOrDefault(m => Screens.All(s => s.MonitorDeviceName != m.DeviceName));
        if (monitor is null)
            return;

        var screenVm = new ScreenViewModel(monitor.DeviceName, monitor.Width, monitor.Height, Push);
        screenVm.AddSlice(new Slice
        {
            Name = "Full",
            InputRect = new RectF(0, 0, _compWidth(), _compHeight()),
            OutputRect = new RectF(0, 0, monitor.Width, monitor.Height),
        });
        Screens.Add(screenVm);
        SelectedScreen = screenVm;
        Push();
    }

    [RelayCommand]
    private void RemoveScreen(ScreenViewModel? screen)
    {
        if (screen is null)
            return;
        if (ReferenceEquals(SelectedScreen, screen))
            SelectedScreen = null;
        Screens.Remove(screen);
        Push();
    }

    [RelayCommand]
    private void ToggleIdentify()
    {
        Identify = !Identify;
        _render.Post(new SetIdentify(Identify));
    }

    // ── Orientação física da tela (T31: "tela 9x16") ────────────────────────
    // TV montada em pé recebendo sinal deitado: o Windows continua reportando
    // 1920×1080 paisagem, então o conteúdo precisa sair GIRADO dentro do quadro
    // pra aparecer certo na TV física. Esses presets configuram o primeiro slice
    // da tela de uma vez: recorte 9:16 centralizado da composição + rotação +
    // saída na tela inteira — sem o operador digitar número nenhum.
    // Se o Windows JÁ reporta retrato (altura > largura na configuração de tela
    // do sistema), a rotação não é necessária — só o recorte 9:16 — e o preset
    // detecta isso sozinho pela resolução reportada.

    [RelayCommand]
    private void ApplyOrientationLandscape(ScreenViewModel? screen)
        => ApplyOrientation(screen, Rotation.R0, portraitCrop: false);

    /// <summary>TV em pé, girada com o topo pra DIREITA (sentido horário).</summary>
    [RelayCommand]
    private void ApplyOrientationPortraitRight(ScreenViewModel? screen)
        => ApplyOrientation(screen, Rotation.R90, portraitCrop: true);

    /// <summary>TV em pé, girada com o topo pra ESQUERDA (sentido anti-horário).</summary>
    [RelayCommand]
    private void ApplyOrientationPortraitLeft(ScreenViewModel? screen)
        => ApplyOrientation(screen, Rotation.R270, portraitCrop: true);

    private void ApplyOrientation(ScreenViewModel? screen, Rotation rotation, bool portraitCrop)
    {
        if (screen is null || screen.Slices.Count == 0) return;
        var slice = screen.Slices[0];

        double compW = CompWidth, compH = CompHeight;
        bool windowsAlreadyPortrait = screen.MonitorHeight > screen.MonitorWidth;

        if (portraitCrop)
        {
            // Proporção visível da TV em pé = (lado menor):(lado maior) do monitor.
            double shortSide = System.Math.Min(screen.MonitorWidth, screen.MonitorHeight);
            double longSide = System.Math.Max(screen.MonitorWidth, screen.MonitorHeight);
            double cropW = System.Math.Min(compW, compH * shortSide / longSide);
            slice.InputX = (compW - cropW) / 2;
            slice.InputY = 0;
            slice.InputWidth = cropW;
            slice.InputHeight = compH;
            // Windows já girado pra retrato → o sistema rotaciona por nós, sinal sai direto.
            slice.Rotation = windowsAlreadyPortrait ? Rotation.R0 : rotation;
        }
        else
        {
            slice.InputX = 0;
            slice.InputY = 0;
            slice.InputWidth = compW;
            slice.InputHeight = compH;
            slice.Rotation = Rotation.R0;
        }

        slice.OutputX = 0;
        slice.OutputY = 0;
        slice.OutputWidth = screen.MonitorWidth;
        slice.OutputHeight = screen.MonitorHeight;

        StatusText = portraitCrop
            ? windowsAlreadyPortrait
                ? $"'{screen.Name}': recorte 9:16 aplicado (Windows já está em retrato, sem girar)."
                : $"'{screen.Name}': recorte 9:16 + rotação {slice.Rotation} aplicados."
            : $"'{screen.Name}': tela deitada (16:9), composição inteira.";
    }

    [RelayCommand]
    private void SavePreset()
    {
        OutputSetupSerializer.Save(PresetName, BuildModel());
        RefreshPresetNames();
        StatusText = $"Preset '{PresetName}' salvo.";
    }

    [RelayCommand]
    private void LoadPreset()
    {
        var setup = OutputSetupSerializer.Load(PresetName);
        if (setup is null)
        {
            StatusText = $"Preset '{PresetName}' não encontrado.";
            return;
        }
        LoadFromModel(setup);
        Push();
        StatusText = $"Preset '{PresetName}' carregado.";
    }

    [RelayCommand]
    private void DeletePreset()
    {
        OutputSetupSerializer.Delete(PresetName);
        RefreshPresetNames();
        StatusText = $"Preset '{PresetName}' excluído.";
    }

    private void RefreshPresetNames()
    {
        PresetNames.Clear();
        foreach (var name in OutputSetupSerializer.ListPresetNames())
            PresetNames.Add(name);
    }

    public void LoadFromModel(OutputSetup setup)
    {
        _suppressPush = true;
        try
        {
            Screens.Clear();
            foreach (var screen in setup.Screens)
            {
                var monitor = AvailableMonitors.FirstOrDefault(m => m.DeviceName == screen.MonitorDeviceName);
                int w = monitor?.Width ?? (int)(screen.Slices.Count > 0 ? screen.Slices[0].OutputRect.Width : 1920);
                int h = monitor?.Height ?? (int)(screen.Slices.Count > 0 ? screen.Slices[0].OutputRect.Height : 1080);
                var screenVm = new ScreenViewModel(screen.MonitorDeviceName, w, h, Push)
                {
                    Name = screen.Name,
                    Enabled = screen.Enabled,
                    Brightness = screen.Color.Brightness,
                    Contrast = screen.Color.Contrast,
                    Gamma = screen.Color.Gamma,
                    SourceLayerIndex = screen.SourceLayerIndex,
                };
                foreach (var slice in screen.Slices)
                    screenVm.AddSlice(slice.Clone());
                Screens.Add(screenVm);
            }
        }
        finally
        {
            _suppressPush = false;
        }
    }

    /// <summary>Snapshot atual do editor como <see cref="OutputSetup"/> — usado ao fechar a janela para o chamador guardar no projeto (T7.2).</summary>
    public OutputSetup ToModel() => BuildModel();

    private OutputSetup BuildModel()
    {
        var setup = new OutputSetup();
        foreach (var screenVm in Screens)
            setup.Screens.Add(screenVm.ToModel());
        return setup;
    }

    public void Push()
    {
        if (_suppressPush)
            return;
        _render.Post(new SyncOutputSetup(BuildModel()));
    }
}

public sealed partial class ScreenViewModel : ObservableObject
{
    private readonly Action _push;
    public string MonitorDeviceName { get; }
    public int MonitorWidth { get; }
    public int MonitorHeight { get; }
    public string MonitorResolutionText => $"Tela: {MonitorWidth}×{MonitorHeight} ({OutputSetupViewModel.FormatAspect(MonitorWidth, MonitorHeight)})";

    [ObservableProperty] private string _name = "Screen";
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private double _brightness;
    [ObservableProperty] private double _contrast = 1.0;
    [ObservableProperty] private double _gamma = 1.0;

    /// <summary>Roteamento layer→monitor (null = composição inteira). Editado pelo
    /// combo rápido na linha SAÍDAS da janela principal; aqui só faz o round-trip
    /// para o editor de slices não apagar a escolha.</summary>
    public int? SourceLayerIndex { get; set; }

    public ObservableCollection<SliceViewModel> Slices { get; } = [];

    public ScreenViewModel(string monitorDeviceName, int monitorWidth, int monitorHeight, Action push)
    {
        MonitorDeviceName = monitorDeviceName;
        MonitorWidth = monitorWidth;
        MonitorHeight = monitorHeight;
        _push = push;
        Name = monitorDeviceName;
    }

    public void AddSlice(Slice model)
    {
        Slices.Add(new SliceViewModel(model, _push));
        _push();
    }

    [RelayCommand]
    private void AddSlice()
    {
        AddSlice(new Slice
        {
            Name = $"Recorte {Slices.Count + 1}",
            InputRect = new RectF(0, 0, MonitorWidth, MonitorHeight),
            OutputRect = new RectF(0, 0, MonitorWidth, MonitorHeight),
        });
    }

    [RelayCommand]
    private void RemoveSlice(SliceViewModel? slice)
    {
        if (slice is null)
            return;
        Slices.Remove(slice);
        _push();
    }

    [RelayCommand]
    private void DuplicateSlice(SliceViewModel? slice)
    {
        if (slice is null)
            return;
        var copy = slice.ToModel().Clone();
        copy.Name += " (cópia)";
        AddSlice(copy);
    }

    partial void OnNameChanged(string value) => _push();
    partial void OnEnabledChanged(bool value) => _push();
    partial void OnBrightnessChanged(double value) => _push();
    partial void OnContrastChanged(double value) => _push();
    partial void OnGammaChanged(double value) => _push();

    public Screen ToModel()
    {
        var screen = new Screen
        {
            Name = Name,
            MonitorDeviceName = MonitorDeviceName,
            Enabled = Enabled,
            Color = new ColorCorrection { Brightness = Brightness, Contrast = Contrast, Gamma = Gamma },
            SourceLayerIndex = SourceLayerIndex,
        };
        foreach (var sliceVm in Slices)
            screen.Slices.Add(sliceVm.ToModel());
        return screen;
    }
}

public sealed partial class SliceViewModel : ObservableObject
{
    private readonly Action _push;
    private readonly Guid _id;

    [ObservableProperty] private string _name;
    [ObservableProperty] private double _inputX;
    [ObservableProperty] private double _inputY;
    [ObservableProperty] private double _inputWidth;
    [ObservableProperty] private double _inputHeight;
    [ObservableProperty] private double _outputX;
    [ObservableProperty] private double _outputY;
    [ObservableProperty] private double _outputWidth;
    [ObservableProperty] private double _outputHeight;
    [ObservableProperty] private Rotation _rotation;
    [ObservableProperty] private bool _flipH;
    [ObservableProperty] private bool _flipV;

    public SliceViewModel(Slice model, Action push)
    {
        _push = push;
        _id = model.Id;
        _name = model.Name;
        _inputX = model.InputRect.X; _inputY = model.InputRect.Y;
        _inputWidth = model.InputRect.Width; _inputHeight = model.InputRect.Height;
        _outputX = model.OutputRect.X; _outputY = model.OutputRect.Y;
        _outputWidth = model.OutputRect.Width; _outputHeight = model.OutputRect.Height;
        _rotation = model.Rotation;
        _flipH = model.FlipH; _flipV = model.FlipV;
    }

    partial void OnNameChanged(string value) => _push();
    partial void OnInputXChanged(double value) => _push();
    partial void OnInputYChanged(double value) => _push();
    partial void OnInputWidthChanged(double value) => _push();
    partial void OnInputHeightChanged(double value) => _push();
    partial void OnOutputXChanged(double value) => _push();
    partial void OnOutputYChanged(double value) => _push();
    partial void OnOutputWidthChanged(double value) => _push();
    partial void OnOutputHeightChanged(double value) => _push();
    partial void OnRotationChanged(Rotation value) => _push();
    partial void OnFlipHChanged(bool value) => _push();
    partial void OnFlipVChanged(bool value) => _push();

    public Slice ToModel() => new()
    {
        Id = _id,
        Name = Name,
        InputRect = new RectF((float)InputX, (float)InputY, (float)InputWidth, (float)InputHeight),
        OutputRect = new RectF((float)OutputX, (float)OutputY, (float)OutputWidth, (float)OutputHeight),
        Rotation = Rotation,
        FlipH = FlipH,
        FlipV = FlipV,
    };
}
