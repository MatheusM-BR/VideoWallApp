using System.IO;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkiaSharp;

namespace VideoWallRW.App.ViewModels;

public enum EditorTool { Crop, Canvas, Brush, Eraser, Eyedropper, Bucket, Shape, Text }

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
    private readonly Stack<SKBitmap> _undoStack = new();
    private readonly Stack<SKBitmap> _redoStack = new();

    [ObservableProperty] private SKBitmap _bitmap;
    [ObservableProperty] private EditorTool _tool = EditorTool.Crop;
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

    public List<AspectPreset> AspectPresets { get; }
    public IReadOnlyList<ShapeKind> Shapes { get; } = Enum.GetValues<ShapeKind>();

    /// <summary>Paleta estilo Paint (hex) — clique escolhe; conta-gotas também atualiza.</summary>
    public IReadOnlyList<string> Palette { get; } =
    [
        "#000000", "#7F7F7F", "#FFFFFF", "#880015", "#ED1C24", "#FF7F27",
        "#FFF200", "#22B14C", "#00A2E8", "#3F48CC", "#A349A4", "#B97A57",
    ];

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
        _undoStack.Push(Bitmap.Copy());
        while (_undoStack.Count > MaxUndo)
        {
            var arr = _undoStack.ToArray();
            arr[^1].Dispose();
            _undoStack.Clear();
            for (int i = arr.Length - 2; i >= 0; i--) _undoStack.Push(arr[i]);
        }
        foreach (var b in _redoStack) b.Dispose();
        _redoStack.Clear();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    [RelayCommand]
    private void Undo()
    {
        if (_undoStack.Count == 0) return;
        _redoStack.Push(Bitmap);
        Bitmap = _undoStack.Pop();
        BitmapChanged?.Invoke();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    [RelayCommand]
    private void Redo()
    {
        if (_redoStack.Count == 0) return;
        _undoStack.Push(Bitmap);
        Bitmap = _redoStack.Pop();
        BitmapChanged?.Invoke();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    // ── Pincel / borracha ────────────────────────────────────────────────────

    public void BeginStroke() => PushUndo();

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
        var cropped = new SKBitmap(new SKImageInfo(r.Width, r.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(cropped))
            canvas.DrawBitmap(Bitmap, r, new SKRect(0, 0, r.Width, r.Height));
        Bitmap = cropped;
        BitmapChanged?.Invoke();
    }

    /// <summary>Esticar/encolher a imagem inteira para o novo tamanho.</summary>
    public void ApplyResize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        PushUndo();
        var resized = Bitmap.Resize(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul),
            SKFilterQuality.High);
        if (resized is null) return;
        Bitmap = resized;
        BitmapChanged?.Invoke();
    }

    /// <summary>Redimensionar só a TELA: a imagem mantém o tamanho, ancorada no
    /// canto superior esquerdo; área nova fica branca (igual ao Paint).</summary>
    public void ApplyCanvasResize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        PushUndo();
        var canvasBitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(canvasBitmap))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(Bitmap, 0, 0);
        }
        Bitmap = canvasBitmap;
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
    public string SaveTargetPath => Path.Combine(
        AppPaths.EnsureMediaRoot(),
        Path.GetFileNameWithoutExtension(_sourcePath) + "-edit.png");

    [RelayCommand]
    private void Save()
    {
        using var image = SKImage.FromBitmap(Bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using (var stream = File.Create(SaveTargetPath))
            data.SaveTo(stream);
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
        foreach (var b in _undoStack) b.Dispose();
        foreach (var b in _redoStack) b.Dispose();
    }
}
