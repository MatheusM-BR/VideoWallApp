using SkiaSharp;

namespace VideoWallRW.App.ViewModels;

/// <summary>
/// Ferramenta SELEÇÃO: marca um retângulo da imagem e transforma só aquele pedaço —
/// mover e esticar (cantos nos dois eixos, laterais num eixo só).
///
/// Fluxo:
///  1. <see cref="SetSelection"/> marca a área (ainda nada mudou na imagem);
///  2. na primeira mexida, <see cref="LiftSelection"/> "descola" os pixels da área
///     para um pedaço flutuante e deixa o buraco com a cor de fundo (um passo de
///     desfazer é criado aqui, com a imagem ANTES de descolar);
///  3. <see cref="SetFloatingRect"/> move/estica o pedaço (só prévia, sem perda);
///  4. <see cref="CommitSelection"/> grava o pedaço na posição/tamanho finais
///     (Enter, clicar fora, trocar de ferramenta, salvar). <see cref="CancelSelection"/>
///     volta ao estado de antes de descolar.
///
/// Com camadas mescladas age na imagem; no modo camadas, na camada de imagem sob a
/// seleção (ou no fundo, se não houver).
/// </summary>
public sealed partial class ImageEditorViewModel
{
    private SKBitmap? _floatPixels;
    private EditorLayer? _floatTarget;
    private SKRect _floatRect;

    /// <summary>Retângulo da seleção em coordenadas da imagem (null = nada selecionado).
    /// Com o pedaço flutuando, é a posição/tamanho atuais dele.</summary>
    public SKRect? Selection { get; private set; }

    /// <summary>Pedaço descolado esperando Aplicar/Cancelar.</summary>
    public bool IsSelectionFloating => _floatPixels is not null;

    public bool HasSelection => Selection is not null;

    /// <summary>Tamanho original do pedaço descolado (pra mostrar % de esticamento).</summary>
    public SKSizeI FloatingSourceSize => _floatPixels is null ? default : new SKSizeI(_floatPixels.Width, _floatPixels.Height);

    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(Selection));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsSelectionFloating));
        BitmapChanged?.Invoke();
    }

    /// <summary>Marca (ou limpa, com null) a área — limitada à tela, mínimo 1 px.
    /// Não mexe na imagem. Com um pedaço flutuando, aplica ele antes.</summary>
    public void SetSelection(SKRect? rect)
    {
        CommitSelection();
        if (rect is { } r)
        {
            var n = Normalize(r);
            n.Intersect(SKRect.Create(0, 0, Bitmap.Width, Bitmap.Height));
            Selection = n.Width >= 1 && n.Height >= 1 ? n : null;
        }
        else Selection = null;
        RaiseSelectionChanged();
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void SelectAll()
    {
        if (Tool != EditorTool.Select) Tool = EditorTool.Select;
        SetSelection(SKRect.Create(0, 0, Bitmap.Width, Bitmap.Height));
        StatusText = "Imagem inteira selecionada. Arraste as alças para esticar.";
    }

    /// <summary>Descola os pixels da seleção. Devolve false se não há o que descolar.</summary>
    public bool LiftSelection()
    {
        if (_floatPixels is not null) return true;
        if (Selection is not { } sel) return false;

        // Merged: a imagem. Modo camadas: a camada de imagem sob o centro da seleção.
        var target = LayerMode ? PaintLayerAt(new SKPoint(sel.MidX, sel.MidY)) : null;
        var source = target?.Bitmap ?? Bitmap;
        var local = SKRectI.Round(target is null ? sel : ToLayerLocal(target, sel));
        local.Intersect(new SKRectI(0, 0, source.Width, source.Height));
        if (local.Width < 1 || local.Height < 1) return false;

        PushUndo();
        var pixels = new SKBitmap(new SKImageInfo(local.Width, local.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var c = new SKCanvas(pixels))
        {
            c.Clear(SKColors.Transparent);
            c.DrawBitmap(source, local, SKRect.Create(0, 0, local.Width, local.Height));
        }
        // Buraco: na imagem volta à cor de fundo (branco, ou transparente se a arte era
        // transparente); numa camada vira transparência.
        using (var c = new SKCanvas(source))
        using (var hole = new SKPaint { Color = target is null ? _backgroundFill : SKColors.Transparent, BlendMode = SKBlendMode.Src })
            c.DrawRect(local, hole);
        source.NotifyPixelsChanged();

        _floatPixels = pixels;
        _floatTarget = target;
        SKRect localF = local; // SKRectI → SKRect (conversão implícita)
        _floatRect = target is null ? localF : FromLayerLocal(target, localF);
        Selection = _floatRect;
        RaiseSelectionChanged();
        return true;
    }

    /// <summary>Nova posição/tamanho do pedaço (só prévia). Mínimo 1×1 px.</summary>
    public void SetFloatingRect(SKRect rect)
    {
        if (_floatPixels is null) return;
        var n = Normalize(rect);
        if (n.Width < 1) n.Right = n.Left + 1;
        if (n.Height < 1) n.Bottom = n.Top + 1;
        _floatRect = n;
        Selection = n;
        RaiseSelectionChanged();
    }

    /// <summary>Grava o pedaço na posição/tamanho atuais. Sem pedaço flutuando, não faz nada.</summary>
    public void CommitSelection()
    {
        if (_floatPixels is not { } pixels) return;
        var target = _floatTarget is { } t && Layers.Contains(t) ? t : null;
        var dest = target?.Bitmap ?? Bitmap;
        var rect = target is null ? _floatRect : ToLayerLocal(target, _floatRect);
        using (var c = new SKCanvas(dest))
        using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true })
            c.DrawBitmap(pixels, rect, paint);
        dest.NotifyPixelsChanged();
        DropFloating();
        // A área continua marcada no lugar novo: dá pra esticar de novo em seguida.
        Selection = Normalize(_floatRect);
        StatusText = $"Seleção aplicada: {(int)Math.Round(_floatRect.Width)} × {(int)Math.Round(_floatRect.Height)} px.";
        RaiseSelectionChanged();
    }

    /// <summary>Esc: com pedaço flutuando, volta a imagem a como estava antes de
    /// descolar; sem pedaço, só desmarca.</summary>
    public void CancelSelection()
    {
        if (_floatPixels is not null)
        {
            DropFloating();
            Selection = null;
            UndoWithoutRedo();
            StatusText = "Transformação cancelada.";
        }
        else Selection = null;
        RaiseSelectionChanged();
    }

    /// <summary>Delete: apaga a área (fica a cor de fundo / transparência).</summary>
    public void DeleteSelection()
    {
        if (_floatPixels is null && !LiftSelection()) return;
        DropFloating();          // o buraco já foi feito ao descolar — é só não colar de volta
        Selection = null;
        StatusText = "Área apagada.";
        RaiseSelectionChanged();
    }

    /// <summary>Atalhos do painel: esticar até a tela inteira / só na largura / só na altura.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void StretchSelection(string? mode)
    {
        if (Selection is null || !LiftSelection()) return;
        var r = _floatRect;
        var full = SKRect.Create(0, 0, Bitmap.Width, Bitmap.Height);
        SetFloatingRect(mode switch
        {
            "largura" => new SKRect(full.Left, r.Top, full.Right, r.Bottom),
            "altura" => new SKRect(r.Left, full.Top, r.Right, full.Bottom),
            _ => full,
        });
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ApplySelection() => CommitSelection();

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void CancelTransform() => CancelSelection();

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void DeleteSelectionArea() => DeleteSelection();

    /// <summary>Campo de largura/altura do painel: redimensiona a partir do canto
    /// superior esquerdo.</summary>
    public void ResizeSelectionTo(float width, float height)
    {
        if (Selection is null || width < 1 || height < 1 || !LiftSelection()) return;
        SetFloatingRect(SKRect.Create(_floatRect.Left, _floatRect.Top, width, height));
    }

    /// <summary>Desenha o pedaço flutuante (prévia), por cima de tudo.</summary>
    public void DrawFloatingSelection(SKCanvas canvas)
    {
        if (_floatPixels is null) return;
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
        canvas.DrawBitmap(_floatPixels, _floatRect, paint);
    }

    private void DropFloating()
    {
        _floatPixels?.Dispose();
        _floatPixels = null;
        _floatTarget = null;
    }

    /// <summary>Volta o último passo de desfazer SEM deixá-lo no refazer (cancelar uma
    /// transformação não é uma ação que se refaça).</summary>
    private void UndoWithoutRedo()
    {
        if (_undoStack.Count == 0) return;
        RestoreSnapshot(_undoStack.Pop());
        BitmapChanged?.Invoke();
        HistoryRestored?.Invoke();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    private static SKRect Normalize(SKRect r) => new(
        Math.Min(r.Left, r.Right), Math.Min(r.Top, r.Bottom),
        Math.Max(r.Left, r.Right), Math.Max(r.Top, r.Bottom));

    private static SKRect ToLayerLocal(EditorLayer layer, SKRect imageRect)
    {
        var a = layer.ToLocal(new SKPoint(imageRect.Left, imageRect.Top));
        var b = layer.ToLocal(new SKPoint(imageRect.Right, imageRect.Bottom));
        return Normalize(new SKRect(a.X, a.Y, b.X, b.Y));
    }

    private static SKRect FromLayerLocal(EditorLayer layer, SKRect localRect)
    {
        if (layer.Bitmap is not { } bmp || bmp.Width == 0 || bmp.Height == 0) return localRect;
        float sx = layer.Width / bmp.Width, sy = layer.Height / bmp.Height;
        return new SKRect(layer.X + localRect.Left * sx, layer.Y + localRect.Top * sy,
                          layer.X + localRect.Right * sx, layer.Y + localRect.Bottom * sy);
    }
}
