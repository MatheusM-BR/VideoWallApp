using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkiaSharp;

namespace VideoWallRW.App.ViewModels;

public enum EditorTool { Crop, Canvas, Brush, Eraser, Eyedropper, Bucket, Shape, Text, Layers }

public enum ShapeKind { Retângulo, Elipse, Linha }

public sealed record AspectPreset(string Name, double? Ratio); // null = livre

/// <summary>
/// Editor de imagem estilo Paint 3D (Fase 10 T10.5, revisado): crop com presets de
/// proporção, pincel, borracha, conta-gotas, balde de tinta (flood fill com
/// tolerância), formas geométricas (contorno ou preenchida), texto, redimensionar
/// imagem/tela e undo/redo. Documento = <see cref="SKBitmap"/> BGRA mutável; cada
/// operação destrutiva empilha uma cópia (máx. 20 níveis).
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

    [ObservableProperty] private SKBitmap _bitmap;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPaintingTool))]
    private EditorTool _tool = EditorTool.Crop;

    public bool IsPaintingTool => Tool is EditorTool.Brush or EditorTool.Eraser or EditorTool.Bucket or EditorTool.Eyedropper;
    [ObservableProperty] private SKColor _brushColor = SKColors.Red;
    [ObservableProperty] private string _brushColorHex = "#FF0000";
    [ObservableProperty] private int _brushSize = 12;
    [ObservableProperty] private string _textInput = "";
    [ObservableProperty] private int _textSize = 32;
    [ObservableProperty] private AspectPreset? _selectedPreset;
    [ObservableProperty] private ShapeKind _shape = ShapeKind.Retângulo;
    [ObservableProperty] private bool _shapeFilled = true;
    [ObservableProperty] private int _bucketTolerance = 32;
    [ObservableProperty] private string _statusText = "";

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
        using (var stream = File.OpenRead(sourcePath))
        {
            var decoded = SKBitmap.Decode(stream) ?? throw new InvalidOperationException($"Não foi possível abrir '{sourcePath}'.");
            // Normaliza pra BGRA8888 — o balde de tinta opera direto nos bytes.
            if (decoded.ColorType != SKColorType.Bgra8888)
            {
                var converted = decoded.Copy(SKColorType.Bgra8888);
                decoded.Dispose();
                decoded = converted ?? throw new InvalidOperationException("Formato de pixel não suportado.");
            }
            _bitmap = decoded;
        }

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
        Bitmap.Dispose();
        foreach (var layer in Layers) layer.Dispose();
        Layers.Clear();
        Bitmap = snapshot.Bitmap;
        foreach (var layer in snapshot.Layers)
        {
            AttachLayer(layer);
            Layers.Add(layer);
        }
        ActiveLayer = Layers.LastOrDefault();
        OnPropertyChanged(nameof(HasLayers));
    }

    [RelayCommand]
    private void Undo()
    {
        if (_undoStack.Count == 0) return;
        _redoStack.Push(CaptureSnapshot());
        RestoreSnapshot(_undoStack.Pop());
        BitmapChanged?.Invoke();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    [RelayCommand]
    private void Redo()
    {
        if (_redoStack.Count == 0) return;
        _undoStack.Push(CaptureSnapshot());
        RestoreSnapshot(_redoStack.Pop());
        BitmapChanged?.Invoke();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    // ── Pincel / borracha ────────────────────────────────────────────────────

    public void BeginStroke() => PushUndo();
    public void BeginLayerTransform() => PushUndo();

    public void PaintStroke(SKPoint from, SKPoint to)
    {
        using var canvas = new SKCanvas(Bitmap);
        using var paint = new SKPaint
        {
            Color = Tool == EditorTool.Eraser ? SKColors.White : BrushColor,
            StrokeWidth = BrushSize,
            StrokeCap = SKStrokeCap.Round,
            IsAntialias = true,
        };
        canvas.DrawLine(from, to, paint);
    }

    // ── Conta-gotas ──────────────────────────────────────────────────────────

    public void PickColorAt(SKPoint p)
    {
        int x = (int)p.X, y = (int)p.Y;
        if (x < 0 || y < 0 || x >= Bitmap.Width || y >= Bitmap.Height) return;
        var color = Bitmap.GetPixel(x, y);
        BrushColor = color;
        BrushColorHex = $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";
        StatusText = $"Cor capturada: {BrushColorHex}";

        ColorHistory.Remove(BrushColorHex);
        ColorHistory.Insert(0, BrushColorHex);
        while (ColorHistory.Count > MaxColorHistory)
            ColorHistory.RemoveAt(ColorHistory.Count - 1);
    }

    // ── Balde de tinta (flood fill com tolerância) ───────────────────────────

    public void FloodFillAt(SKPoint p)
    {
        int startX = (int)p.X, startY = (int)p.Y;
        int w = Bitmap.Width, h = Bitmap.Height;
        if (startX < 0 || startY < 0 || startX >= w || startY >= h) return;
        if (Bitmap.GetPixel(startX, startY) == BrushColor) return; // já é a cor destino

        PushUndo();

        // Bytes BGRA copiados pra um buffer gerenciado, preenchidos, e devolvidos —
        // sem código unsafe, e ordens de grandeza mais rápido que Get/SetPixel.
        nint ptr = Bitmap.GetPixels();
        int byteCount = Bitmap.ByteCount;
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
        Bitmap.NotifyPixelsChanged();
        BitmapChanged?.Invoke();
    }

    // ── Formas geométricas ───────────────────────────────────────────────────

    public void ApplyShape(SKPoint a, SKPoint b)
    {
        PushUndo();
        using var canvas = new SKCanvas(Bitmap);
        using var paint = new SKPaint
        {
            Color = BrushColor,
            IsAntialias = true,
            Style = ShapeFilled && Shape != ShapeKind.Linha ? SKPaintStyle.Fill : SKPaintStyle.Stroke,
            StrokeWidth = BrushSize,
        };

        var rect = SKRect.Create(
            System.Math.Min(a.X, b.X), System.Math.Min(a.Y, b.Y),
            System.Math.Abs(b.X - a.X), System.Math.Abs(b.Y - a.Y));

        switch (Shape)
        {
            case ShapeKind.Retângulo: canvas.DrawRect(rect, paint); break;
            case ShapeKind.Elipse: canvas.DrawOval(rect, paint); break;
            case ShapeKind.Linha: canvas.DrawLine(a, b, paint); break;
        }
        BitmapChanged?.Invoke();
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
            layer.X *= scaleX;
            layer.Y *= scaleY;
            layer.Scale *= Math.Min(scaleX, scaleY);
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
            canvas.Clear(TransparentCanvas ? SKColors.Transparent : SKColors.White);
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

    // ── Texto ────────────────────────────────────────────────────────────────

    public void ApplyText(SKPoint position)
    {
        if (string.IsNullOrWhiteSpace(TextInput)) return;
        PushUndo();
        using var canvas = new SKCanvas(Bitmap);
        using var paint = new SKPaint
        {
            Color = BrushColor,
            IsAntialias = true,
            TextSize = TextSize,
        };
        canvas.DrawText(TextInput, position, paint);
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

            var layer = new EditorLayer(bmp, Path.GetFileNameWithoutExtension(path),
                (Bitmap.Width - bmp.Width * scale) / 2f,
                (Bitmap.Height - bmp.Height * scale) / 2f)
            { Scale = scale };

            PushUndo();
            AttachLayer(layer);
            Layers.Add(layer);
            ActiveLayer = layer;
            OnPropertyChanged(nameof(HasLayers));
            StatusText = $"Camada adicionada: {layer.Name} — arraste na imagem pra posicionar";
            BitmapChanged?.Invoke();
        }
        catch (Exception ex)
        {
            StatusText = $"Falha ao adicionar camada: {ex.Message}";
        }
    }

    private void AttachLayer(EditorLayer layer)
        => layer.PropertyChanged += (_, _) => BitmapChanged?.Invoke();

    partial void OnActiveLayerChanged(EditorLayer? value) => BitmapChanged?.Invoke();

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

        foreach (var layer in Layers) layer.Dispose();
        Layers.Clear();
        ActiveLayer = null;
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

    public string SaveTargetPath => Path.Combine(
        AppPaths.EnsureMediaRoot(),
        Path.GetFileNameWithoutExtension(_sourcePath) + "-edit.png");

    [RelayCommand]
    private void Save()
    {
        // Salva a composição (base + camadas), mas mantém as camadas separadas em
        // memória — salvar não pode "queimar" o que o operador ainda está ajustando.
        var composed = ComposeFlattened();
        using var image = SKImage.FromBitmap(composed);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using (var stream = File.Create(SaveTargetPath))
            data.SaveTo(stream);
        if (!ReferenceEquals(composed, Bitmap)) composed.Dispose();
        StatusText = $"Salvo: {SaveTargetPath} — arraste o chip \"🖼\" até uma célula da grade";
        // Editando a imagem de uma célula: salvar já atualiza o clip no ar.
        _onSaved?.Invoke(SaveTargetPath, Path.GetFileNameWithoutExtension(_sourcePath));
    }

    /// <summary>Garante o PNG salvo em disco (usado pelo chip de arrastar).</summary>
    public string EnsureSaved()
    {
        Save();
        return SaveTargetPath;
    }

    public void Dispose()
    {
        Bitmap.Dispose();
        foreach (var layer in Layers) layer.Dispose();
        foreach (var snapshot in _undoStack) snapshot.Dispose();
        foreach (var snapshot in _redoStack) snapshot.Dispose();
    }
}
