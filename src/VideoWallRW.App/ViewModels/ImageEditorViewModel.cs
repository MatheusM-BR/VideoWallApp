using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkiaSharp;

namespace VideoWallRW.App.ViewModels;

public enum EditorTool { None, Crop, Brush, Text }

public sealed record AspectPreset(string Name, double? Ratio); // null = livre

/// <summary>
/// Editor de imagem estilo Paint (Fase 10 T10.5) — versão enxuta: crop com presets de
/// proporção, pincel, texto, undo/redo. Sem borracha/conta-gotas/formas/balde/zoom-pan
/// (simplificação deliberada dado o tamanho da fase; documentado em DESENVOLVIMENTO.md).
/// Documento = <see cref="SKBitmap"/> mutável; cada operação destrutiva empilha uma
/// cópia em <see cref="_undoStack"/> antes de aplicar (máx. 20 níveis).
/// </summary>
public sealed partial class ImageEditorViewModel : ObservableObject, IDisposable
{
    private const int MaxUndo = 20;
    private readonly string _sourcePath;
    private readonly Action<string, string> _onSendToCell;
    private readonly Stack<SKBitmap> _undoStack = new();
    private readonly Stack<SKBitmap> _redoStack = new();

    [ObservableProperty] private SKBitmap _bitmap;
    [ObservableProperty] private EditorTool _tool = EditorTool.Crop;
    [ObservableProperty] private SKColor _brushColor = SKColors.Red;
    [ObservableProperty] private int _brushSize = 12;
    [ObservableProperty] private string _textInput = "";
    [ObservableProperty] private int _textSize = 32;
    [ObservableProperty] private AspectPreset? _selectedPreset;
    [ObservableProperty] private SKRect? _cropRect;
    [ObservableProperty] private string _statusText = "";

    public List<AspectPreset> AspectPresets { get; }

    public event Action? BitmapChanged;

    public ImageEditorViewModel(string sourcePath, Action<string, string> onSendToCell,
        double? compositionAspect = null, IReadOnlyList<double>? sliceAspects = null)
    {
        _sourcePath = sourcePath;
        _onSendToCell = onSendToCell;
        using (var stream = File.OpenRead(sourcePath))
            _bitmap = SKBitmap.Decode(stream) ?? throw new InvalidOperationException($"Não foi possível abrir '{sourcePath}'.");

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

    private void PushUndo()
    {
        _undoStack.Push(Bitmap.Copy());
        while (_undoStack.Count > MaxUndo)
        {
            // Descarta o snapshot mais antigo (base da pilha) — Stack não suporta
            // remoção do fundo diretamente, então reconstrói via array.
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

    /// <summary>Chamado pela janela a cada movimento do pincel — desenha direto no
    /// bitmap atual (sem PushUndo por ponto; o snapshot é feito uma vez no mouse-down).</summary>
    public void PaintStroke(SKPoint from, SKPoint to)
    {
        using var canvas = new SKCanvas(Bitmap);
        using var paint = new SKPaint
        {
            Color = BrushColor,
            StrokeWidth = BrushSize,
            StrokeCap = SKStrokeCap.Round,
            IsAntialias = true,
        };
        canvas.DrawLine(from, to, paint);
    }

    public void BeginStroke() => PushUndo();

    public void ApplyCrop(SKRect rect)
    {
        var r = SKRectI.Round(rect);
        r.Intersect(new SKRectI(0, 0, Bitmap.Width, Bitmap.Height));
        if (r.Width <= 0 || r.Height <= 0) return;

        PushUndo();
        var cropped = new SKBitmap(r.Width, r.Height);
        using (var canvas = new SKCanvas(cropped))
            canvas.DrawBitmap(Bitmap, r, new SKRect(0, 0, r.Width, r.Height));
        Bitmap = cropped;
        CropRect = null;
        BitmapChanged?.Invoke();
    }

    public void ApplyResize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        PushUndo();
        var resized = Bitmap.Resize(new SKImageInfo(width, height), SKFilterQuality.High);
        Bitmap = resized;
        BitmapChanged?.Invoke();
    }

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

    private string SavePath => Path.Combine(
        Path.GetDirectoryName(_sourcePath)!,
        Path.GetFileNameWithoutExtension(_sourcePath) + "-edit.png");

    [RelayCommand]
    private void Save()
    {
        using var image = SKImage.FromBitmap(Bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(SavePath);
        data.SaveTo(stream);
        StatusText = $"Salvo: {Path.GetFileName(SavePath)}";
    }

    [RelayCommand]
    private void SaveAndSendToCell()
    {
        Save();
        _onSendToCell(SavePath, Path.GetFileNameWithoutExtension(_sourcePath));
    }

    public void Dispose()
    {
        Bitmap.Dispose();
        foreach (var b in _undoStack) b.Dispose();
        foreach (var b in _redoStack) b.Dispose();
    }
}
