using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkiaSharp;

namespace VideoWallRW.App.ViewModels;

public enum EditorTool { Crop, Canvas, Brush, Eraser, Eyedropper, Bucket, Shape, Text, Layers, Select }

public enum ShapeKind { Retângulo, Elipse, Linha }

public sealed record AspectPreset(string Name, double? Ratio); // null = livre

/// <summary>
/// Editor de imagem estilo Paint 3D (Fase 10 T10.5, revisado): crop com presets de
/// proporção, pincel, borracha, conta-gotas, balde de tinta (flood fill com
/// tolerância), formas e texto como camadas editáveis, redimensionar imagem/tela e
/// undo/redo (máx. 20 níveis).
///
/// Documento = FUNDO (<see cref="Bitmap"/>, do tamanho da tela) + CAMADAS por cima.
/// A imagem aberta entra como a primeira camada ("Imagem original"): dá pra mover e
/// redimensionar sobre o fundo, e os pincéis pintam na camada de imagem selecionada
/// (ou no fundo, quando nenhuma está selecionada).
/// </summary>
public sealed partial class ImageEditorViewModel : ObservableObject, IDisposable
{
    private const int MaxUndo = 20;
    private readonly string _sourcePath;
    private readonly Action<string, string>? _onSaved; // célula em edição: Salvar atualiza no ar
    private sealed record EditorSnapshot(SKBitmap Bitmap, List<EditorLayer> Layers) : IDisposable
    {
        public void Dispose()
        {
            Bitmap.Dispose();
            foreach (var layer in Layers) layer.Dispose();
        }
    }
    private readonly Stack<EditorSnapshot> _undoStack = new();
    private readonly Stack<EditorSnapshot> _redoStack = new();
    private string? _lastSavedPath;

    [ObservableProperty] private SKBitmap _bitmap;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPaintingTool))]
    private EditorTool _tool = EditorTool.Crop;

    public bool IsPaintingTool => Tool is EditorTool.Brush or EditorTool.Eraser or EditorTool.Bucket or EditorTool.Eyedropper;
    [ObservableProperty] private SKColor _brushColor = SKColors.Red;
    [ObservableProperty] private string _brushColorHex = "#FF0000";
    [ObservableProperty] private int _brushSize = 12;
    [ObservableProperty] private string _textInput = "Texto";
    [ObservableProperty] private int _textSize = 48;
    [ObservableProperty] private AspectPreset? _selectedPreset;
    [ObservableProperty] private ShapeKind _shape = ShapeKind.Retângulo;
    [ObservableProperty] private bool _shapeFilled = true;
    [ObservableProperty] private int _bucketTolerance = 32;
    [ObservableProperty] private string _statusText = "";

    /// <summary>Ímãs: ao mover uma camada ela "gruda" no centro/bordas da tela e de
    /// outras camadas, com guia desenhada. Botão no rodapé da área de edição.</summary>
    [ObservableProperty] private bool _snapEnabled = true;

    /// <summary>Cor que a borracha devolve ao FUNDO: branco, ou transparente quando a
    /// imagem original já tinha transparência (logo/PNG).</summary>
    private readonly SKColor _backgroundFill;

    /// <summary>Fixar proporção (corte e tela) — igual ao Paint 3D.</summary>
    [ObservableProperty] private bool _lockAspect = true;

    /// <summary>Painel Tela: marcado = a imagem estica junto com a tela; desmarcado =
    /// só a tela muda (imagem ancorada no canto, área nova branca).</summary>
    [ObservableProperty] private bool _resizeImageWithCanvas = true;

    /// <summary>Painel Tela: área nova entra TRANSPARENTE em vez de branca (Paint 3D
    /// tem o mesmo interruptor). Útil pra logo/banner que vai por cima de outra arte —
    /// com fundo branco a moldura apareceria como uma tarja no telão.</summary>
    [ObservableProperty] private bool _transparentCanvas;

    public List<AspectPreset> AspectPresets { get; }
    public IReadOnlyList<ShapeKind> Shapes { get; } = Enum.GetValues<ShapeKind>();

    /// <summary>Paleta estilo Paint (hex) — clique escolhe; conta-gotas também atualiza.</summary>
    public IReadOnlyList<string> Palette { get; } =
    [
        "#000000", "#7F7F7F", "#FFFFFF", "#880015", "#ED1C24", "#FF7F27",
        "#FFF200", "#22B14C", "#00A2E8", "#3F48CC", "#A349A4", "#B97A57",
    ];

    private const int MaxColorHistory = 12;

    /// <summary>Cores pegas com o conta-gotas, mais recente primeiro — clicável igual à Palette.</summary>
    public ObservableCollection<string> ColorHistory { get; } = [];

    public event Action? BitmapChanged;

    public ImageEditorViewModel(string sourcePath, Action<string, string>? onSaved = null,
        double? compositionAspect = null, IReadOnlyList<double>? sliceAspects = null)
    {
        _sourcePath = sourcePath;
        _onSaved = onSaved;
        SKBitmap decoded;
        using (var stream = File.OpenRead(sourcePath))
            decoded = SKBitmap.Decode(stream) ?? throw new InvalidOperationException($"Não foi possível abrir '{sourcePath}'.");

        // A imagem original vira CAMADA, destacada do fundo; o fundo nasce do mesmo
        // tamanho, branco (ou transparente se a arte já tinha transparência) — então
        // salvar sem mexer em nada sai idêntico ao original.
        var original = EditorLayer.FromImage(decoded, "Imagem original", 0, 0);
        _backgroundFill = HasTransparency(original.Bitmap!) ? SKColors.Transparent : SKColors.White;
        _bitmap = new SKBitmap(new SKImageInfo(original.Bitmap!.Width, original.Bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var bg = new SKCanvas(_bitmap)) bg.Clear(_backgroundFill);
        // Padrão: camadas MESCLADAS — a foto já é a própria imagem (pinta, preenche e
        // aumenta a tela direto nela). O modo camadas é ligado pelo operador.
        using (var bg = new SKCanvas(_bitmap)) original.Draw(bg);
        original.Dispose();

        // Texto num tamanho que aparece na arte (32px some num banner 1920×1080).
        _textSize = Math.Clamp(_bitmap.Height / 10, 24, 400);

        AspectPresets =
        [
            new AspectPreset("Original", _bitmap.Width / (double)_bitmap.Height),
            new AspectPreset("Livre", null),
            new AspectPreset("16:9", 16.0 / 9),
            new AspectPreset("9:16", 9.0 / 16),
            new AspectPreset("1:1", 1.0),
            new AspectPreset("4:3", 4.0 / 3),
        ];
        if (compositionAspect is { } ca)
            AspectPresets.Add(new AspectPreset("Composição", ca));
        if (sliceAspects is not null)
            for (int i = 0; i < sliceAspects.Count; i++)
                AspectPresets.Add(new AspectPreset($"Slice {i + 1}", sliceAspects[i]));
        // "Fixar proporção" já vem ativo por padrão (T9) — sem um preset com razão
        // definida ele não travava nada, então o padrão é a proporção original da imagem.
        SelectedPreset = AspectPresets[0];
    }

    private static bool HasTransparency(SKBitmap bitmap)
    {
        var span = MemoryMarshal.Cast<byte, uint>(bitmap.GetPixelSpan());
        foreach (uint px in span)
            if ((px >> 24) != 0xFF) return true;
        return false;
    }

    // ── Alvo da pintura ──────────────────────────────────────────────────────
    // Pincel/borracha/balde agem na camada de IMAGEM selecionada. Com texto/forma (ou
    // nada) selecionado, agem na camada de imagem mais alta — senão a tinta iria pro
    // fundo, escondido atrás da imagem, e pareceria que o pincel não funciona. O fundo
    // só recebe tinta quando o operador escolhe "Fundo" no painel Camadas.

    private bool _paintOnBackground;

    public EditorLayer? PaintLayer =>
        ActiveLayer is { Kind: EditorLayerKind.Image, Visible: true } active ? active
        : _paintOnBackground ? null
        : Layers.LastOrDefault(l => l is { Kind: EditorLayerKind.Image, Visible: true });

    /// <summary>Botão "Fundo (tela)": deseleciona as camadas e manda os pincéis pro fundo.</summary>
    public void SelectBackground()
    {
        ActiveLayer = null;
        _paintOnBackground = true;
        OnPropertyChanged(nameof(PaintTargetName));
    }

    public string PaintTargetName => PaintLayer?.Name ?? (LayerMode ? "Fundo" : "Imagem");

    // ── Modo camadas × camadas mescladas ─────────────────────────────────────

    /// <summary>
    /// Desligado (padrão): tudo é mesclado na imagem. Forma/texto continuam ajustáveis
    /// enquanto selecionados, e se fundem na imagem ao trocar de ferramenta, clicar
    /// fora ou criar outro elemento. Ligado: cada elemento fica como camada separada.
    /// </summary>
    [ObservableProperty] private bool _layerMode;

    private bool _merging, _restoring;

    partial void OnLayerModeChanged(bool value)
    {
        if (!value) MergeLayersExcept(null);
        StatusText = value
            ? "Modo camadas ligado: formas, textos e imagens ficam em camadas separadas."
            : "Camadas mescladas: cada elemento se funde na imagem ao terminar de ajustar.";
        OnPropertyChanged(nameof(PaintTargetName));
    }

    partial void OnToolChanged(EditorTool value)
    {
        // Sair da Seleção aplica o que estiver sendo esticado e desmarca a área.
        if (value != EditorTool.Select && (Selection is not null || IsSelectionFloating))
        {
            CommitSelection();
            Selection = null;
            ClearSelectionShape();
            RaiseSelectionChanged();
        }
        MergeLayersExcept(null);
    }

    /// <summary>Funde na imagem as camadas (menos <paramref name="keep"/>) quando o modo
    /// camadas está desligado. Não cria passo de desfazer próprio: o Ctrl+Z volta para
    /// antes do elemento ter sido criado/alterado, como num editor sem camadas.</summary>
    private void MergeLayersExcept(EditorLayer? keep)
    {
        if (LayerMode || _merging || _restoring) return;
        var toMerge = Layers.Where(l => !ReferenceEquals(l, keep)).ToList();
        if (toMerge.Count == 0) return;
        _merging = true;
        try
        {
            using (var canvas = new SKCanvas(Bitmap))
                foreach (var layer in toMerge) layer.Draw(canvas);
            Bitmap.NotifyPixelsChanged();
            foreach (var layer in toMerge) Layers.Remove(layer);
            if (ActiveLayer is not null && !Layers.Contains(ActiveLayer)) ActiveLayer = null;
            foreach (var layer in toMerge) layer.Dispose();
            OnPropertyChanged(nameof(HasLayers));
            OnPropertyChanged(nameof(PaintTargetName));
            BitmapChanged?.Invoke();
        }
        finally { _merging = false; }
    }

    /// <summary>
    /// Onde a tinta cai NESTE ponto: a camada de pintura se o ponto está dentro dela;
    /// senão a camada de imagem visível mais alta que contém o ponto; senão o fundo.
    /// Sem isto, depois de aumentar a tela a área nova (branca) não aceitava tinta —
    /// o pincel continuava mirando a "Imagem original", que não cobre a área nova.
    /// </summary>
    public EditorLayer? PaintLayerAt(SKPoint p)
    {
        if (PaintLayer is { } preferred && preferred.Contains(p)) return preferred;
        for (int i = Layers.Count - 1; i >= 0; i--)
            if (Layers[i] is { Kind: EditorLayerKind.Image, Visible: true } l && l.Contains(p))
                return l;
        return null; // fundo (tela)
    }

    private EditorLayer? _strokeLayer;

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public void SetColorHex(string hex)
    {
        if (SKColor.TryParse(hex, out var color))
        {
            BrushColor = color;
            BrushColorHex = hex;
        }
    }

    private void PushUndo()
    {
        _undoStack.Push(CaptureSnapshot());
        while (_undoStack.Count > MaxUndo)
        {
            var arr = _undoStack.ToArray();
            arr[^1].Dispose();
            _undoStack.Clear();
            for (int i = arr.Length - 2; i >= 0; i--) _undoStack.Push(arr[i]);
        }
        foreach (var snapshot in _redoStack) snapshot.Dispose();
        _redoStack.Clear();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    private EditorSnapshot CaptureSnapshot()
        => new(Bitmap.Copy(), Layers.Select(layer => layer.Clone()).ToList());

    private void RestoreSnapshot(EditorSnapshot snapshot)
    {
        // ORDEM IMPORTA: primeiro troca tudo pelo estado restaurado e só DEPOIS libera
        // o antigo. Liberar antes fazia o Layers.Clear()/ActiveLayer disparar o
        // redesenho (síncrono, via BitmapChanged) lendo um SKBitmap já liberado —
        // acesso inválido de memória nativa, que derruba o app sem passar pelo log.
        var oldBitmap = Bitmap;
        var oldLayers = Layers.ToList();
        _restoring = true;
        try
        {
            Bitmap = snapshot.Bitmap;
            Layers.Clear();
            foreach (var layer in snapshot.Layers)
            {
                AttachLayer(layer);
                Layers.Add(layer);
            }
            ActiveLayer = Layers.LastOrDefault();
        }
        finally { _restoring = false; }
        OnPropertyChanged(nameof(HasLayers));
        OnPropertyChanged(nameof(PaintTargetName));

        oldBitmap.Dispose();
        foreach (var layer in oldLayers) layer.Dispose();
    }

    /// <summary>Disparado depois de Desfazer/Refazer: a janela refaz os quadros de
    /// recorte/tela, que podem ter ficado com o tamanho da imagem anterior.</summary>
    public event Action? HistoryRestored;

    [RelayCommand]
    private void Undo()
    {
        // Durante uma transformação da Seleção, desfazer = cancelar a transformação.
        if (IsSelectionFloating) { CancelSelection(); return; }
        if (_undoStack.Count == 0) return;
        if (Selection is not null) { Selection = null; ClearSelectionShape(); RaiseSelectionChanged(); }
        _redoStack.Push(CaptureSnapshot());
        RestoreSnapshot(_undoStack.Pop());
        BitmapChanged?.Invoke();
        HistoryRestored?.Invoke();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    [RelayCommand]
    private void Redo()
    {
        if (_redoStack.Count == 0 || IsSelectionFloating) return;
        if (Selection is not null) { Selection = null; ClearSelectionShape(); RaiseSelectionChanged(); }
        _undoStack.Push(CaptureSnapshot());
        RestoreSnapshot(_redoStack.Pop());
        BitmapChanged?.Invoke();
        HistoryRestored?.Invoke();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    // ── Pincel / borracha ────────────────────────────────────────────────────

    /// <summary>Começo do traço: o alvo (camada ou fundo) é decidido pelo ponto
    /// onde o clique começou e fica fixo até soltar.</summary>
    public void BeginStroke(SKPoint start)
    {
        PushUndo();
        _strokeLayer = PaintLayerAt(start);
    }
    public void BeginLayerTransform() => PushUndo();

    public void PaintStroke(SKPoint from, SKPoint to)
    {
        var layer = _strokeLayer is { } s && Layers.Contains(s) ? s : null;
        var target = layer?.Bitmap ?? Bitmap;
        float scale = layer?.LocalScale ?? 1f;
        if (layer is not null) { from = layer.ToLocal(from); to = layer.ToLocal(to); }

        bool erase = Tool == EditorTool.Eraser;
        using var canvas = new SKCanvas(target);
        using var paint = new SKPaint
        {
            // Borracha: na camada abre transparência (aparece o fundo); no fundo volta
            // à cor de fundo (branco, ou transparente se a arte era transparente).
            Color = erase ? (layer is null ? _backgroundFill : SKColors.Transparent) : BrushColor,
            BlendMode = erase ? SKBlendMode.Src : SKBlendMode.SrcOver,
            StrokeWidth = BrushSize * scale,
            StrokeCap = SKStrokeCap.Round,
            IsAntialias = true,
        };
        canvas.DrawLine(from, to, paint);
        target.NotifyPixelsChanged();
    }

    // ── Conta-gotas ──────────────────────────────────────────────────────────

    public void PickColorAt(SKPoint p)
    {
        int x = (int)p.X, y = (int)p.Y;
        if (x < 0 || y < 0 || x >= Bitmap.Width || y >= Bitmap.Height) return;
        // Pega a cor do que se VÊ (fundo + camadas), não só do fundo.
        var composed = ComposeFlattened();
        var color = composed.GetPixel(x, y);
        if (!ReferenceEquals(composed, Bitmap)) composed.Dispose();
        if (color.Alpha == 0) return; // área transparente: não tem cor pra pegar
        BrushColor = color;
        BrushColorHex = $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";
        StatusText = $"Cor capturada: {BrushColorHex} — balde selecionado.";

        ColorHistory.Remove(BrushColorHex);
        ColorHistory.Insert(0, BrushColorHex);
        while (ColorHistory.Count > MaxColorHistory)
            ColorHistory.RemoveAt(ColorHistory.Count - 1);

        // Fluxo mais comum: pegou a cor pra preencher. Já entra no balde.
        Tool = EditorTool.Bucket;
    }

    // ── Balde de tinta (flood fill com tolerância) ───────────────────────────

    public void FloodFillAt(SKPoint p)
    {
        var layer = PaintLayerAt(p);
        var fillTarget = layer?.Bitmap ?? Bitmap;
        if (layer is not null) p = layer.ToLocal(p);

        int startX = (int)p.X, startY = (int)p.Y;
        int w = fillTarget.Width, h = fillTarget.Height;
        if (startX < 0 || startY < 0 || startX >= w || startY >= h) return;
        if (fillTarget.GetPixel(startX, startY) == BrushColor) return; // já é a cor destino

        PushUndo();

        // Bytes BGRA copiados pra um buffer gerenciado, preenchidos, e devolvidos —
        // sem código unsafe, e ordens de grandeza mais rápido que Get/SetPixel.
        nint ptr = fillTarget.GetPixels();
        int byteCount = fillTarget.ByteCount;
        byte[] buffer = new byte[byteCount];
        Marshal.Copy(ptr, buffer, 0, byteCount);
        var pixels = MemoryMarshal.Cast<byte, uint>(buffer.AsSpan());

        uint target = pixels[startY * w + startX];
        uint replacement = (uint)((BrushColor.Alpha << 24) | (BrushColor.Red << 16) |
                                  (BrushColor.Green << 8) | BrushColor.Blue);

        int tol = BucketTolerance;
        bool Matches(uint c)
        {
            int db = (int)(c & 0xFF) - (int)(target & 0xFF);
            int dg = (int)((c >> 8) & 0xFF) - (int)((target >> 8) & 0xFF);
            int dr = (int)((c >> 16) & 0xFF) - (int)((target >> 16) & 0xFF);
            return System.Math.Abs(db) <= tol && System.Math.Abs(dg) <= tol && System.Math.Abs(dr) <= tol;
        }

        var stack = new Stack<(int X, int Y)>();
        stack.Push((startX, startY));
        var visited = new bool[w * h];

        while (stack.Count > 0)
        {
            var (x, y) = stack.Pop();
            int idx = y * w + x;
            if (visited[idx] || !Matches(pixels[idx])) continue;
            visited[idx] = true;
            pixels[idx] = replacement;
            if (x > 0) stack.Push((x - 1, y));
            if (x < w - 1) stack.Push((x + 1, y));
            if (y > 0) stack.Push((x, y - 1));
            if (y < h - 1) stack.Push((x, y + 1));
        }

        Marshal.Copy(buffer, 0, ptr, byteCount);
        fillTarget.NotifyPixelsChanged();
        BitmapChanged?.Invoke();
    }

    // ── Formas geométricas (viram camada ao soltar o clique) ─────────────────

    /// <summary>Cria a camada de forma do retângulo arrastado (a→b) e já seleciona —
    /// sem passo de "concluir": fica móvel/redimensionável como qualquer camada.</summary>
    public EditorLayer? AddShapeLayer(SKPoint a, SKPoint b)
    {
        var rect = SKRect.Create(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
        if (rect.Width < 3 && rect.Height < 3) return null; // clique acidental
        // Linha reta na horizontal/vertical: caixa com espessura mínima pra continuar pegável.
        if (Shape == ShapeKind.Linha)
        {
            if (rect.Width < 1) rect = SKRect.Create(rect.Left, rect.Top, 1, rect.Height);
            if (rect.Height < 1) rect = SKRect.Create(rect.Left, rect.Top, rect.Width, 1);
        }
        bool rising = (b.X - a.X) * (b.Y - a.Y) < 0;
        var layer = EditorLayer.FromShape(Shape, rect, rising, ShapeFilled, BrushSize, BrushColor);
        InsertLayer(layer, $"{Shape} inserido — arraste pra mover, cantos pra redimensionar");
        return layer;
    }

    // ── Texto (vira camada) ──────────────────────────────────────────────────

    /// <summary>Clique: texto no tamanho do campo, com o canto no ponto clicado.</summary>
    public EditorLayer? AddTextLayer(SKPoint topLeft) => AddTextLayer(topLeft, TextSize);

    /// <summary>Arrastou uma caixa: o tamanho da fonte sai da ALTURA da caixa.</summary>
    public EditorLayer? AddTextLayer(SKRect box)
    {
        int size = (int)Math.Clamp(Math.Round(box.Height / EditorLayer.LineHeightPerPoint()), 6, 2000);
        return AddTextLayer(new SKPoint(box.Left, box.Top), size);
    }

    private EditorLayer? AddTextLayer(SKPoint topLeft, int fontSize)
    {
        string text = string.IsNullOrWhiteSpace(TextInput) ? "Texto" : TextInput;
        var layer = EditorLayer.FromText(text, fontSize, BrushColor, topLeft.X, topLeft.Y);
        _syncingFromLayer = true;
        TextSize = fontSize;
        TextInput = text;
        _syncingFromLayer = false;
        InsertLayer(layer, "Texto inserido — digite no campo pra trocar, cantos redimensionam");
        return layer;
    }

    private void InsertLayer(EditorLayer layer, string status)
    {
        PushUndo();
        AttachLayer(layer);
        Layers.Add(layer);
        ActiveLayer = layer;
        OnPropertyChanged(nameof(HasLayers));
        StatusText = status;
        BitmapChanged?.Invoke();
    }

    // Editar campos com texto/forma selecionado altera a CAMADA (como no Paint 3D).
    // Um nível de desfazer por "sessão de edição" da camada, não um por tecla.
    private bool _syncingFromLayer;
    private EditorLayer? _editUndoTakenFor;

    private bool BeginLayerPropertyEdit(EditorLayerKind kind, out EditorLayer layer)
    {
        layer = ActiveLayer!;
        if (_syncingFromLayer || ActiveLayer is null || ActiveLayer.Kind != kind) return false;
        if (!ReferenceEquals(_editUndoTakenFor, ActiveLayer))
        {
            PushUndo();
            _editUndoTakenFor = ActiveLayer;
        }
        return true;
    }

    partial void OnTextInputChanged(string value)
    {
        if (Tool != EditorTool.Text || string.IsNullOrEmpty(value) || !BeginLayerPropertyEdit(EditorLayerKind.Text, out var layer)) return;
        layer.Text = value;
        layer.FitTextBox();
    }

    partial void OnTextSizeChanged(int value)
    {
        if (Tool != EditorTool.Text || value < 4 || !BeginLayerPropertyEdit(EditorLayerKind.Text, out var layer)) return;
        layer.FontSize = value;
        layer.FitTextBox();
    }

    partial void OnBrushColorChanged(SKColor value)
    {
        if (Tool == EditorTool.Text && BeginLayerPropertyEdit(EditorLayerKind.Text, out var text)) text.Color = value;
        else if (Tool == EditorTool.Shape && BeginLayerPropertyEdit(EditorLayerKind.Shape, out var shape)) shape.Color = value;
    }

    partial void OnShapeChanged(ShapeKind value)
    {
        if (Tool == EditorTool.Shape && BeginLayerPropertyEdit(EditorLayerKind.Shape, out var layer))
        {
            layer.Shape = value;
            layer.Name = value.ToString();
        }
    }

    partial void OnShapeFilledChanged(bool value)
    {
        if (Tool == EditorTool.Shape && BeginLayerPropertyEdit(EditorLayerKind.Shape, out var layer)) layer.Filled = value;
    }

    partial void OnBrushSizeChanged(int value)
    {
        if (Tool == EditorTool.Shape && value > 0 && BeginLayerPropertyEdit(EditorLayerKind.Shape, out var layer)) layer.StrokeWidth = value;
    }

    /// <summary>Selecionar texto/forma traz as propriedades dele pros campos.</summary>
    private void SyncFieldsFromLayer(EditorLayer? layer)
    {
        if (layer is null || layer.Kind == EditorLayerKind.Image) return;
        _syncingFromLayer = true;
        try
        {
            BrushColor = layer.Color;
            BrushColorHex = $"#{layer.Color.Red:X2}{layer.Color.Green:X2}{layer.Color.Blue:X2}";
            if (layer.Kind == EditorLayerKind.Text)
            {
                TextInput = layer.Text;
                TextSize = (int)Math.Round(layer.FontSize * (layer.Height / Math.Max(layer.NaturalTextSize().Height, 1)));
            }
            else
            {
                Shape = layer.Shape;
                ShapeFilled = layer.Filled;
                BrushSize = (int)Math.Round(layer.StrokeWidth);
            }
        }
        finally { _syncingFromLayer = false; }
    }

    // ── Crop / redimensionar ─────────────────────────────────────────────────

    public void ApplyCrop(SKRect rect)
    {
        var r = SKRectI.Round(rect);
        r.Intersect(new SKRectI(0, 0, Bitmap.Width, Bitmap.Height));
        if (r.Width <= 0 || r.Height <= 0) return;

        PushUndo();
        var oldBitmap = Bitmap;
        var cropped = new SKBitmap(new SKImageInfo(r.Width, r.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(cropped))
            canvas.DrawBitmap(oldBitmap, r, new SKRect(0, 0, r.Width, r.Height));
        Bitmap = cropped;
        oldBitmap.Dispose();
        foreach (var layer in Layers)
        {
            layer.X -= r.Left;
            layer.Y -= r.Top;
        }
        StatusText = $"Recorte aplicado: {r.Width} × {r.Height} px.";
        BitmapChanged?.Invoke();
    }

    /// <summary>Esticar/encolher a imagem inteira para o novo tamanho.</summary>
    public void ApplyResize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        int oldWidth = Bitmap.Width, oldHeight = Bitmap.Height;
        var oldBitmap = Bitmap;
        PushUndo();
        var resized = oldBitmap.Resize(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul),
            SKFilterQuality.High);
        if (resized is null) return;
        Bitmap = resized;
        oldBitmap.Dispose();
        float scaleX = width / (float)Math.Max(oldWidth, 1);
        float scaleY = height / (float)Math.Max(oldHeight, 1);
        foreach (var layer in Layers)
        {
            // Camada que ocupava a tela inteira (ex.: "Imagem original" sem mexer)
            // acompanha a tela nova exatamente — senão ficava menor, com faixas brancas,
            // e parecia que o aumento não tinha sido aplicado.
            if (Math.Abs(layer.X) < 1 && Math.Abs(layer.Y) < 1 &&
                Math.Abs(layer.Width - oldWidth) < 1 && Math.Abs(layer.Height - oldHeight) < 1)
            {
                layer.X = 0; layer.Y = 0; layer.Width = width; layer.Height = height;
                continue;
            }
            // Imagem/texto não deformam: escala uniforme centrada no mesmo ponto relativo.
            float sx = layer.KeepsAspect ? Math.Min(scaleX, scaleY) : scaleX;
            float sy = layer.KeepsAspect ? Math.Min(scaleX, scaleY) : scaleY;
            float cx = (layer.X + layer.Width / 2) * scaleX, cy = (layer.Y + layer.Height / 2) * scaleY;
            layer.Width *= sx;
            layer.Height *= sy;
            if (layer.Kind == EditorLayerKind.Shape) layer.StrokeWidth *= Math.Min(scaleX, scaleY);
            layer.X = cx - layer.Width / 2;
            layer.Y = cy - layer.Height / 2;
        }
        StatusText = $"Imagem e camadas redimensionadas para {width} × {height} px.";
        BitmapChanged?.Invoke();
    }

    /// <summary>Redimensionar só a TELA: a imagem mantém o tamanho e fica
    /// CENTRALIZADA na tela nova (área extra fica branca, igual ao Paint) — não
    /// importa se a tela cresceu pra direita, esquerda, cima ou baixo, o desenho
    /// original sempre aparece no meio, facilitando continuar editando em volta dele.</summary>
    public void ApplyCanvasResize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        PushUndo();
        var oldBitmap = Bitmap;
        var canvasBitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(canvasBitmap))
        {
            canvas.Clear(TransparentCanvas ? SKColors.Transparent : _backgroundFill);
            float offsetX = (width - Bitmap.Width) / 2f;
            float offsetY = (height - Bitmap.Height) / 2f;
            canvas.DrawBitmap(oldBitmap, offsetX, offsetY);
            foreach (var layer in Layers)
            {
                layer.X += offsetX;
                layer.Y += offsetY;
            }
        }
        Bitmap = canvasBitmap;
        oldBitmap.Dispose();
        StatusText = $"Tela alterada para {width} × {height} px; conteúdo centralizado.";
        BitmapChanged?.Invoke();
    }

    // ── Salvar ───────────────────────────────────────────────────────────────

    // Toda edição vai pra pasta padrão da operação (C:\Video Wall), nunca sobrescreve o original.
    // ── Camadas sobrepostas (estilo "adesivo" do Paint 3D) ────────────────────
    // A arte base é o Bitmap; estas ficam POR CIMA, na ordem da lista (a última é a
    // mais alta). Transformações e mudanças estruturais entram no mesmo histórico
    // da imagem base, para desfazer preservar a composição inteira.

    public ObservableCollection<EditorLayer> Layers { get; } = [];

    /// <summary>Camada selecionada — é a que a ferramenta "Camadas" move/redimensiona.</summary>
    [ObservableProperty] private EditorLayer? _activeLayer;

    public bool HasLayers => Layers.Count > 0;

    /// <summary>Importa uma imagem como camada nova, centralizada e já cabendo na arte
    /// (se vier maior que a base, entra reduzida — senão ficaria fora da tela).</summary>
    public void AddImageLayer(string path)
    {
        try
        {
            var bmp = SKBitmap.Decode(path);
            if (bmp is null) { StatusText = $"Não consegui abrir: {Path.GetFileName(path)}"; return; }

            float scale = 1f;
            float maxW = Bitmap.Width * 0.8f, maxH = Bitmap.Height * 0.8f;
            if (bmp.Width > maxW || bmp.Height > maxH)
                scale = Math.Min(maxW / bmp.Width, maxH / bmp.Height);

            var layer = EditorLayer.FromImage(bmp, Path.GetFileNameWithoutExtension(path),
                (Bitmap.Width - bmp.Width * scale) / 2f,
                (Bitmap.Height - bmp.Height * scale) / 2f, scale);
            InsertLayer(layer, $"Camada adicionada: {layer.Name} — arraste na imagem pra posicionar");
        }
        catch (Exception ex)
        {
            StatusText = $"Falha ao adicionar camada: {ex.Message}";
        }
    }

    private void AttachLayer(EditorLayer layer)
        => layer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(EditorLayer.Visible) or nameof(EditorLayer.Name))
                OnPropertyChanged(nameof(PaintTargetName));
            BitmapChanged?.Invoke();
        };

    partial void OnActiveLayerChanged(EditorLayer? value)
    {
        // Camadas mescladas: ao selecionar outra (ou nenhuma), a anterior se funde.
        MergeLayersExcept(value);
        _editUndoTakenFor = null;
        if (value is not null) _paintOnBackground = false;
        SyncFieldsFromLayer(value);
        OnPropertyChanged(nameof(PaintTargetName));
        BitmapChanged?.Invoke();
    }

    /// <summary>Centraliza a camada selecionada na tela (botões do painel Camadas).</summary>
    [RelayCommand]
    private void RotateLayer(string? direction)
    {
        if (ActiveLayer is not { } layer) return;
        PushUndo();
        float a = layer.Rotation + (direction == "anti" ? -90 : 90);
        layer.Rotation = NormalizeAngle(a);
        StatusText = $"Camada girada: {layer.Rotation:0.#}°.";
        BitmapChanged?.Invoke();
    }

    /// <summary>Espelha a camada selecionada (como se vê: com giro, o giro inverte junto).</summary>
    [RelayCommand]
    private void FlipLayer(string? axis)
    {
        if (ActiveLayer is not { } layer) return;
        PushUndo();
        if (axis == "v") layer.FlipY = !layer.FlipY; else layer.FlipX = !layer.FlipX;
        layer.Rotation = NormalizeAngle(-layer.Rotation);
        StatusText = axis == "v" ? "Camada espelhada na vertical." : "Camada espelhada na horizontal.";
        BitmapChanged?.Invoke();
    }

    [RelayCommand]
    private void ResetLayerRotation()
    {
        if (ActiveLayer is not { } layer || !layer.IsTransformed) return;
        PushUndo();
        layer.Rotation = 0; layer.FlipX = false; layer.FlipY = false;
        StatusText = "Giro e espelho da camada desfeitos.";
        BitmapChanged?.Invoke();
    }

    public static float NormalizeAngle(float degrees)
    {
        float a = degrees % 360f;
        if (a > 180) a -= 360;
        if (a <= -180) a += 360;
        return a;
    }

    [RelayCommand]
    private void CenterLayer(string? axis)
    {
        if (ActiveLayer is not { } layer) return;
        PushUndo();
        if (axis is null or "both" or "h") layer.X = (Bitmap.Width - layer.Width) / 2f;
        if (axis is null or "both" or "v") layer.Y = (Bitmap.Height - layer.Height) / 2f;
    }

    /// <summary>Faz a camada selecionada ocupar a tela toda (imagem/texto mantêm proporção, cabendo).</summary>
    [RelayCommand]
    private void FitLayerToCanvas()
    {
        if (ActiveLayer is not { } layer) return;
        PushUndo();
        float w = Bitmap.Width, h = Bitmap.Height;
        if (layer.KeepsAspect)
        {
            float s = Math.Min(w / Math.Max(layer.Width, 1), h / Math.Max(layer.Height, 1));
            layer.Width *= s;
            layer.Height *= s;
        }
        else
        {
            layer.Width = w;
            layer.Height = h;
        }
        layer.X = (w - layer.Width) / 2f;
        layer.Y = (h - layer.Height) / 2f;
    }

    [RelayCommand]
    private void RemoveLayer(EditorLayer? layer)
    {
        layer ??= ActiveLayer;
        if (layer is null) return;
        PushUndo();
        Layers.Remove(layer);
        if (ReferenceEquals(ActiveLayer, layer)) ActiveLayer = Layers.LastOrDefault();
        layer.Dispose();
        OnPropertyChanged(nameof(HasLayers));
        BitmapChanged?.Invoke();
    }

    [RelayCommand]
    private void MoveLayerUp(EditorLayer? layer)
    {
        layer ??= ActiveLayer;
        int i = layer is null ? -1 : Layers.IndexOf(layer);
        if (i < 0 || i >= Layers.Count - 1) return;
        PushUndo();
        Layers.Move(i, i + 1);
        BitmapChanged?.Invoke();
    }

    [RelayCommand]
    private void MoveLayerDown(EditorLayer? layer)
    {
        layer ??= ActiveLayer;
        int i = layer is null ? -1 : Layers.IndexOf(layer);
        if (i <= 0) return;
        PushUndo();
        Layers.Move(i, i - 1);
        BitmapChanged?.Invoke();
    }

    /// <summary>Queima todas as camadas na arte base. Desfazível (é destrutivo).</summary>
    [RelayCommand]
    private void FlattenLayers()
    {
        if (Layers.Count == 0) return;
        PushUndo();
        using (var canvas = new SKCanvas(Bitmap))
            foreach (var layer in Layers) layer.Draw(canvas);

        var merged = Layers.ToList();
        Layers.Clear();
        ActiveLayer = null;
        foreach (var layer in merged) layer.Dispose(); // só depois de sair da lista/tela
        OnPropertyChanged(nameof(HasLayers));
        StatusText = "Camadas mescladas na imagem.";
        BitmapChanged?.Invoke();
    }

    /// <summary>
    /// Base + camadas num bitmap só. Usado pra salvar (o arquivo tem que sair com tudo)
    /// sem destruir a separação em memória — o operador segue editando as camadas
    /// depois de salvar.
    /// </summary>
    public SKBitmap ComposeFlattened()
    {
        // Pedaço da Seleção ainda flutuando entra no arquivo como está na tela.
        if (IsSelectionFloating)
        {
            var withFloat = new SKBitmap(new SKImageInfo(Bitmap.Width, Bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            using var c = new SKCanvas(withFloat);
            c.Clear(SKColors.Transparent);
            c.DrawBitmap(Bitmap, 0, 0);
            foreach (var layer in Layers) layer.Draw(c);
            DrawFloatingSelection(c);
            return withFloat;
        }
        if (Layers.Count == 0) return Bitmap;
        var flat = new SKBitmap(new SKImageInfo(Bitmap.Width, Bitmap.Height,
            SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(flat))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(Bitmap, 0, 0);
            foreach (var layer in Layers) layer.Draw(canvas);
        }
        return flat;
    }

    /// <summary>
    /// Arquivo salvo: na MESMA pasta da imagem de origem (a pasta do leilão, no caso da
    /// Agenda/PDF), com o nome pela orientação — "16x9 - Edited.png" (paisagem/quadrado)
    /// ou "9x16 - Edited.png" (retrato). Se já existir, a edição nova entra como
    /// "16x9 - Edited v2.png", "v3"… — nunca apaga uma edição anterior nem a origem.
    /// Dentro da MESMA sessão do editor, salvar de novo atualiza o arquivo que esta
    /// sessão criou (senão cada clique em Salvar / arraste do chip geraria uma versão).
    /// Origem em cache/temporário (não é pasta de operação) → pasta padrão C:\Video Wall.
    /// </summary>
    public string SaveTargetPath
    {
        get
        {
            string orientation = Orientation(Bitmap.Width, Bitmap.Height);
            if (_sessionFiles.TryGetValue(orientation, out var mine)) return mine;
            string folder = SaveFolder();
            for (int version = 1; ; version++)
            {
                string candidate = Path.Combine(folder, EditedFileName(Bitmap.Width, Bitmap.Height, version));
                if (!File.Exists(candidate) &&
                    !string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(_sourcePath), StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
        }
    }

    /// <summary>Arquivos que ESTA sessão do editor já gravou, por orientação.</summary>
    private readonly Dictionary<string, string> _sessionFiles = [];

    private static string Orientation(int width, int height) => height > width ? "9x16" : "16x9";

    public static string EditedFileName(int width, int height, int version = 1)
        => Orientation(width, height) + " - Edited" + (version > 1 ? $" v{version}" : "") + ".png";

    private string SaveFolder()
    {
        string? folder = Path.GetDirectoryName(Path.GetFullPath(_sourcePath));
        if (folder is null || !Directory.Exists(folder)) return AppPaths.EnsureMediaRoot();
        string appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoWallRW");
        bool transient = folder.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)
                      || folder.StartsWith(appData, StringComparison.OrdinalIgnoreCase);
        return transient ? AppPaths.EnsureMediaRoot() : folder;
    }

    /// <summary>Disparado depois de gravar o arquivo (caminho completo) — a janela
    /// principal usa pra abrir a Biblioteca direto na pasta, com o arquivo selecionado.</summary>
    public event Action<string>? FileSaved;

    [RelayCommand]
    private void Save()
    {
        // Salva a composição (base + camadas), mas mantém as camadas separadas em
        // memória — salvar não pode "queimar" o que o operador ainda está ajustando.
        string target = SaveTargetPath;
        var composed = ComposeFlattened();
        using var image = SKImage.FromBitmap(composed);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using (var stream = File.Create(target))
            data.SaveTo(stream);
        if (!ReferenceEquals(composed, Bitmap)) composed.Dispose();
        _lastSavedPath = target;
        _sessionFiles[Orientation(Bitmap.Width, Bitmap.Height)] = target;
        StatusText = $"Salvo: {target} — arraste o chip \"🖼\" até uma célula da grade";
        // Editando a imagem de uma célula: salvar já atualiza o clip no ar.
        _onSaved?.Invoke(target, Path.GetFileNameWithoutExtension(_sourcePath));
        FileSaved?.Invoke(target);
    }

    /// <summary>Garante o PNG salvo em disco (usado pelo chip de arrastar).</summary>
    public string EnsureSaved()
    {
        Save();
        return _lastSavedPath ?? SaveTargetPath;
    }

    public void Dispose()
    {
        DropFloating();
        ClearSelectionShape();
        Bitmap.Dispose();
        foreach (var layer in Layers) layer.Dispose();
        foreach (var snapshot in _undoStack) snapshot.Dispose();
        foreach (var snapshot in _redoStack) snapshot.Dispose();
    }
}
