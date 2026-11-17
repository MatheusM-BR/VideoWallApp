using SkiaSharp;

namespace VideoWallRW.App.ViewModels;

/// <summary>
/// Ferramenta SELEÇÃO: marca um retângulo da imagem e transforma só aquele pedaço —
/// mover, esticar, girar e espelhar — além de copiar, recortar e colar.
///
/// Fluxo:
///  1. <see cref="SetSelection"/> marca a área (ainda nada mudou na imagem);
///  2. na primeira mexida, <see cref="LiftSelection"/> "descola" os pixels da área
///     para um pedaço flutuante e deixa o buraco com a cor de fundo (um passo de
///     desfazer é criado aqui, com a imagem ANTES de descolar);
///  3. <see cref="SetFloatingRect"/>, <see cref="SetSelectionAngle"/>, espelhar e girar
///     90° mexem só no pedaço (prévia, sem perda);
///  4. <see cref="CommitSelection"/> grava o pedaço (Enter, clicar fora, trocar de
///     ferramenta, salvar). <see cref="CancelSelection"/> volta a como estava.
///
/// O retângulo <see cref="Selection"/> é o pedaço SEM giro (centro + tamanho); o giro
/// fica em <see cref="SelectionAngle"/>, em graus, em torno do centro.
/// Com camadas mescladas age na imagem; no modo camadas, na camada de imagem sob a
/// seleção (ou no fundo, se não houver).
/// </summary>
public enum SelectShape { Retângulo, Laço, Varinha }

public sealed partial class ImageEditorViewModel
{
    /// <summary>Como a área é marcada: retângulo arrastado, laço (contorno livre à mão)
    /// ou varinha mágica (clique pega a região da mesma cor).</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private SelectShape _selectMode = SelectShape.Retângulo;

    /// <summary>Borda suave, em px: a área sai esfumada nas bordas ao mover/copiar/apagar.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private int _selectFeather;

    /// <summary>Tolerância de cor da varinha mágica (0 = só a cor exata).</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private int _wandTolerance = 32;

    /// <summary>Formato da área quando não é retângulo: alfa 0..255 do tamanho de
    /// <see cref="Selection"/> (laço e varinha). Null = a área é o retângulo inteiro.</summary>
    private SKBitmap? _selectionMask;

    /// <summary>Contorno pra desenhar na tela (laço/varinha), em coordenadas da imagem.</summary>
    public SKPath? SelectionOutline { get; private set; }

    public bool HasShapedSelection => _selectionMask is not null;

    private SKBitmap? _floatPixels;
    private EditorLayer? _floatTarget;
    private SKRect _floatRect;
    private float _floatAngle;

    /// <summary>Área copiada (Ctrl+C/Ctrl+X). Compartilhada entre janelas do editor,
    /// pra copiar de uma imagem e colar em outra.</summary>
    private static SKBitmap? s_clipboard;

    public SKRect? Selection { get; private set; }
    public float SelectionAngle => _floatPixels is null ? 0 : _floatAngle;
    public bool IsSelectionFloating => _floatPixels is not null;
    public bool HasSelection => Selection is not null;
    public static bool HasCopiedArea => s_clipboard is not null;

    /// <summary>Tamanho original do pedaço descolado (pra mostrar % de esticamento).</summary>
    public SKSizeI FloatingSourceSize => _floatPixels is null ? default : new SKSizeI(_floatPixels.Width, _floatPixels.Height);

    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(Selection));
        OnPropertyChanged(nameof(SelectionAngle));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsSelectionFloating));
        OnPropertyChanged(nameof(HasShapedSelection));
        BitmapChanged?.Invoke();
    }

    private void ClearSelectionShape()
    {
        _selectionMask?.Dispose();
        _selectionMask = null;
        SelectionOutline?.Dispose();
        SelectionOutline = null;
    }

    /// <summary>Laço: marca a área dentro do contorno desenhado à mão.</summary>
    public void SetSelectionPath(SKPath path)
    {
        CommitSelection();
        ClearSelectionShape();
        var bounds = path.TightBounds;
        bounds.Intersect(SKRect.Create(0, 0, Bitmap.Width, Bitmap.Height));
        var box = SKRectI.Round(bounds);
        if (box.Width < 2 || box.Height < 2) { Selection = null; RaiseSelectionChanged(); return; }

        var mask = new SKBitmap(new SKImageInfo(box.Width, box.Height, SKColorType.Alpha8, SKAlphaType.Premul));
        using (var c = new SKCanvas(mask))
        using (var paint = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Fill })
        {
            c.Clear(SKColors.Transparent);
            c.Translate(-box.Left, -box.Top);
            c.DrawPath(path, paint);
        }
        _selectionMask = mask;
        SelectionOutline = new SKPath(path);
        Selection = box;
        StatusText = "Área marcada no laço. Arraste por dentro pra mover, alças pra esticar.";
        RaiseSelectionChanged();
    }

    /// <summary>Varinha mágica: marca a região CONTÍNUA com cor parecida ao ponto
    /// clicado (do que se vê: imagem + camadas), conforme <see cref="WandTolerance"/>.</summary>
    public void SelectByColorAt(SKPoint p)
    {
        CommitSelection();
        ClearSelectionShape();
        int sx = (int)p.X, sy = (int)p.Y, w = Bitmap.Width, h = Bitmap.Height;
        if (sx < 0 || sy < 0 || sx >= w || sy >= h) { Selection = null; RaiseSelectionChanged(); return; }

        var flat = ComposeFlattened();
        var pixels = new byte[flat.ByteCount];
        System.Runtime.InteropServices.Marshal.Copy(flat.GetPixels(), pixels, 0, pixels.Length);
        int rowBytes = flat.RowBytes;
        if (!ReferenceEquals(flat, Bitmap)) flat.Dispose();

        int Idx(int x, int y) => y * rowBytes + x * 4;
        int t = Idx(sx, sy), tol = WandTolerance;
        byte tb = pixels[t], tg = pixels[t + 1], tr = pixels[t + 2], ta = pixels[t + 3];
        bool Match(int x, int y)
        {
            int i = Idx(x, y);
            return Math.Abs(pixels[i] - tb) <= tol && Math.Abs(pixels[i + 1] - tg) <= tol &&
                   Math.Abs(pixels[i + 2] - tr) <= tol && Math.Abs(pixels[i + 3] - ta) <= tol;
        }

        var hit = new bool[w * h];
        int minX = sx, maxX = sx, minY = sy, maxY = sy;
        var stack = new Stack<(int X, int Y)>();
        stack.Push((sx, sy));
        while (stack.Count > 0)
        {
            var (x, y) = stack.Pop();
            int k = y * w + x;
            if (hit[k] || !Match(x, y)) continue;
            hit[k] = true;
            if (x < minX) minX = x; if (x > maxX) maxX = x;
            if (y < minY) minY = y; if (y > maxY) maxY = y;
            if (x > 0) stack.Push((x - 1, y));
            if (x < w - 1) stack.Push((x + 1, y));
            if (y > 0) stack.Push((x, y - 1));
            if (y < h - 1) stack.Push((x, y + 1));
        }

        int bw = maxX - minX + 1, bh = maxY - minY + 1;
        var mask = new SKBitmap(new SKImageInfo(bw, bh, SKColorType.Alpha8, SKAlphaType.Premul));
        var alpha = new byte[mask.RowBytes * bh];
        using var region = new SKRegion();
        for (int y = 0; y < bh; y++)
        {
            int runStart = -1;
            for (int x = 0; x <= bw; x++)
            {
                bool on = x < bw && hit[(y + minY) * w + x + minX];
                if (on) alpha[y * mask.RowBytes + x] = 255;
                if (on && runStart < 0) runStart = x;
                else if (!on && runStart >= 0)
                {
                    region.Op(new SKRectI(runStart + minX, y + minY, x + minX, y + minY + 1), SKRegionOperation.Union);
                    runStart = -1;
                }
            }
        }
        System.Runtime.InteropServices.Marshal.Copy(alpha, 0, mask.GetPixels(), alpha.Length);
        mask.NotifyPixelsChanged();

        // Área do tamanho da imagem inteira com cor única (ex.: fundo liso) = retângulo.
        _selectionMask = mask;
        SelectionOutline = region.GetBoundaryPath();
        Selection = SKRect.Create(minX, minY, bw, bh);
        StatusText = "Área da mesma cor marcada. Delete apaga, Ctrl+C copia; arraste pra mover.";
        RaiseSelectionChanged();
    }

    /// <summary>Máscara alfa → imagem branca com aquele alfa. Desenhar um Alpha8 direto
    /// conta como "cobertura" no Skia: com DstIn, onde a máscara é 0 o destino ficava
    /// intacto em vez de sumir. Como imagem colorida, o recorte sai certo.</summary>
    private static SKBitmap ToColorMask(SKBitmap alphaMask)
    {
        var color = new SKBitmap(new SKImageInfo(alphaMask.Width, alphaMask.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var c = new SKCanvas(color);
        c.Clear(SKColors.Transparent);
        using var paint = new SKPaint { Color = SKColors.White };
        c.DrawBitmap(alphaMask, 0, 0, paint);
        return color;
    }

    /// <summary>Máscara efetiva (formato + borda suave) do tamanho de <paramref name="size"/>;
    /// null quando é retângulo sem borda suave (aí não precisa de máscara).</summary>
    private SKBitmap? EffectiveMask(SKSizeI size)
    {
        if (_selectionMask is null && SelectFeather <= 0) return null;
        var result = new SKBitmap(new SKImageInfo(size.Width, size.Height, SKColorType.Alpha8, SKAlphaType.Premul));
        using var c = new SKCanvas(result);
        c.Clear(SKColors.Transparent);
        var dest = SKRect.Create(0, 0, size.Width, size.Height);
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true, FilterQuality = SKFilterQuality.Medium };
        if (SelectFeather > 0)
        {
            // Borda suave: o contorno vai a zero na borda da área e a 100% "feather" px pra dentro.
            float sigma = SelectFeather / 2.5f;
            paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, sigma);
            if (_selectionMask is null)
            {
                c.DrawRect(SKRect.Inflate(dest, -SelectFeather / 2f, -SelectFeather / 2f), paint);
                return result;
            }
            paint.MaskFilter = null;
            paint.ImageFilter = SKImageFilter.CreateBlur(sigma, sigma);
        }
        c.DrawBitmap(_selectionMask!, dest, paint);
        return result;
    }

    /// <summary>Marca (ou limpa, com null) a área — limitada à tela, mínimo 1 px.
    /// Não mexe na imagem. Com um pedaço flutuando, aplica ele antes.</summary>
    public void SetSelection(SKRect? rect)
    {
        CommitSelection();
        ClearSelectionShape();
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
        StatusText = "Imagem inteira selecionada. Arraste as alças para esticar ou girar.";
    }

    /// <summary>Descola os pixels da seleção. Devolve false se não há o que descolar.</summary>
    public bool LiftSelection()
    {
        if (_floatPixels is not null) return true;
        if (Selection is not { } sel) return false;

        // Mesclado: a imagem. Modo camadas: a camada de imagem sob o centro da seleção.
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
        var holeColor = target is null ? _backgroundFill : SKColors.Transparent;
        using var mask = EffectiveMask(new SKSizeI(local.Width, local.Height));
        if (mask is null)
        {
            // Buraco: na imagem volta à cor de fundo (branco, ou transparente se a arte era
            // transparente); numa camada vira transparência.
            using var c = new SKCanvas(source);
            using var hole = new SKPaint { Color = holeColor, BlendMode = SKBlendMode.Src };
            c.DrawRect(local, hole);
        }
        else
        {
            // Formato (laço/varinha) e/ou borda suave: o pedaço só leva o que está na
            // máscara, e o buraco só abre ali (esfumado onde a máscara é parcial).
            using var colorMask = ToColorMask(mask);
            using (var c = new SKCanvas(pixels))
            using (var keep = new SKPaint { BlendMode = SKBlendMode.DstIn })
                c.DrawBitmap(colorMask, 0, 0, keep);
            using var sc = new SKCanvas(source);
            using var hole = holeColor.Alpha == 0
                ? new SKPaint { BlendMode = SKBlendMode.DstOut }
                : new SKPaint { Color = holeColor, BlendMode = SKBlendMode.SrcOver };
            sc.DrawBitmap(mask, local, hole);
        }
        source.NotifyPixelsChanged();

        SKRect localF = local; // SKRectI → SKRect (conversão implícita)
        BeginFloating(pixels, target, target is null ? localF : FromLayerLocal(target, localF));
        return true;
    }

    private void BeginFloating(SKBitmap pixels, EditorLayer? target, SKRect rect)
    {
        ClearSelectionShape(); // o formato já está nos pixels do pedaço
        _floatPixels = pixels;
        _floatTarget = target;
        _floatRect = rect;
        _floatAngle = 0;
        Selection = rect;
        RaiseSelectionChanged();
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

    /// <summary>Giro livre em graus (sentido horário), em torno do centro da área.</summary>
    public void SetSelectionAngle(float degrees)
    {
        if (!LiftSelection()) return;
        float a = degrees % 360f;
        if (a > 180) a -= 360;
        if (a <= -180) a += 360;
        _floatAngle = a;
        RaiseSelectionChanged();
    }

    /// <summary>Espelha a área. Com giro, espelha como se vê na tela (o giro inverte junto).</summary>
    public void FlipSelection(bool horizontal)
    {
        if (!LiftSelection()) return;
        var src = _floatPixels!;
        var flipped = new SKBitmap(src.Info);
        using (var c = new SKCanvas(flipped))
        {
            c.Clear(SKColors.Transparent);
            if (horizontal) c.Scale(-1, 1, src.Width / 2f, 0);
            else c.Scale(1, -1, 0, src.Height / 2f);
            c.DrawBitmap(src, 0, 0);
        }
        src.Dispose();
        _floatPixels = flipped;
        _floatAngle = -_floatAngle;
        StatusText = horizontal ? "Área espelhada na horizontal." : "Área espelhada na vertical.";
        RaiseSelectionChanged();
    }

    /// <summary>Gira 90° trocando largura por altura, em torno do centro (sem perda).</summary>
    public void RotateSelection90(bool clockwise)
    {
        if (!LiftSelection()) return;
        var src = _floatPixels!;
        var rotated = new SKBitmap(new SKImageInfo(src.Height, src.Width, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var c = new SKCanvas(rotated))
        {
            c.Clear(SKColors.Transparent);
            if (clockwise) { c.Translate(src.Height, 0); c.RotateDegrees(90); }
            else { c.Translate(0, src.Width); c.RotateDegrees(-90); }
            c.DrawBitmap(src, 0, 0);
        }
        src.Dispose();
        _floatPixels = rotated;
        var r = _floatRect;
        _floatRect = SKRect.Create(r.MidX - r.Height / 2, r.MidY - r.Width / 2, r.Height, r.Width);
        Selection = _floatRect;
        StatusText = clockwise ? "Área girada 90° no sentido horário." : "Área girada 90° no sentido anti-horário.";
        RaiseSelectionChanged();
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void FlipSelectionArea(string? axis) => FlipSelection(axis != "v");

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void RotateSelectionArea(string? direction) => RotateSelection90(direction != "anti");

    /// <summary>Grava o pedaço na posição/tamanho/giro atuais. Sem pedaço flutuando, não faz nada.</summary>
    public void CommitSelection()
    {
        if (_floatPixels is not { } pixels) return;
        var target = _floatTarget is { } t && Layers.Contains(t) ? t : null;
        var dest = target?.Bitmap ?? Bitmap;
        using (var c = new SKCanvas(dest))
        {
            if (target is not null)
            {
                c.Scale(dest.Width / Math.Max(target.Width, 0.001f), dest.Height / Math.Max(target.Height, 0.001f));
                c.Translate(-target.X, -target.Y);
            }
            DrawFloating(c, pixels);
        }
        dest.NotifyPixelsChanged();
        var bounds = RotatedBounds(_floatRect, _floatAngle);
        DropFloating();
        // A área continua marcada (no contorno do que foi gravado): dá pra mexer de novo.
        bounds.Intersect(SKRect.Create(0, 0, Bitmap.Width, Bitmap.Height));
        Selection = bounds.Width >= 1 && bounds.Height >= 1 ? bounds : null;
        StatusText = "Seleção aplicada.";
        RaiseSelectionChanged();
    }

    /// <summary>Esc: com pedaço flutuando, volta a imagem a como estava antes de
    /// descolar (ou de colar); sem pedaço, só desmarca.</summary>
    public void CancelSelection()
    {
        if (_floatPixels is not null)
        {
            DropFloating();
            Selection = null;
            UndoWithoutRedo();
            StatusText = "Transformação cancelada.";
        }
        else { Selection = null; ClearSelectionShape(); }
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

    // ── Copiar / recortar / colar ────────────────────────────────────────────

    /// <summary>Ctrl+C: copia o que se VÊ na área (imagem + camadas + pedaço solto).
    /// Devolve a cópia (a janela também manda pra área de transferência do Windows).</summary>
    public SKBitmap? CopySelection()
    {
        if (Selection is not { } sel) return null;
        SKBitmap copy;
        if (_floatPixels is not null && _floatAngle == 0)
        {
            // Pedaço solto sem giro: copia ele inteiro, no tamanho em que está esticado.
            var size = SKRectI.Round(_floatRect);
            copy = new SKBitmap(new SKImageInfo(Math.Max(size.Width, 1), Math.Max(size.Height, 1), SKColorType.Bgra8888, SKAlphaType.Premul));
            using var c = new SKCanvas(copy);
            c.Clear(SKColors.Transparent);
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.High };
            c.DrawBitmap(_floatPixels, SKRect.Create(0, 0, copy.Width, copy.Height), paint);
        }
        else
        {
            var area = SKRectI.Round(RotatedBounds(sel, SelectionAngle));
            area.Intersect(new SKRectI(0, 0, Bitmap.Width, Bitmap.Height));
            if (area.Width < 1 || area.Height < 1) return null;
            var flat = ComposeFlattened();
            copy = new SKBitmap(new SKImageInfo(area.Width, area.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            using (var c = new SKCanvas(copy))
            {
                c.Clear(SKColors.Transparent);
                c.DrawBitmap(flat, area, SKRect.Create(0, 0, area.Width, area.Height));
            }
            if (!ReferenceEquals(flat, Bitmap)) flat.Dispose();
            // Laço/varinha/borda suave: copia só o formato (o resto fica transparente).
            if (SelectionAngle == 0 && area == SKRectI.Round(sel) && EffectiveMask(new SKSizeI(area.Width, area.Height)) is { } mask)
            {
                using (mask)
                using (var colorMask = ToColorMask(mask))
                using (var c = new SKCanvas(copy))
                using (var keep = new SKPaint { BlendMode = SKBlendMode.DstIn })
                    c.DrawBitmap(colorMask, 0, 0, keep);
            }
        }
        s_clipboard?.Dispose();
        s_clipboard = copy;
        OnPropertyChanged(nameof(HasCopiedArea));
        StatusText = $"Área copiada ({copy.Width} × {copy.Height} px). Ctrl+V cola.";
        return copy;
    }

    /// <summary>Ctrl+X: copia e apaga a área.</summary>
    public SKBitmap? CutSelection()
    {
        var copy = CopySelection();
        if (copy is null) return null;
        DeleteSelection();
        StatusText = $"Área recortada ({copy.Width} × {copy.Height} px). Ctrl+V cola.";
        return copy;
    }

    /// <summary>
    /// Ctrl+V: cola <paramref name="image"/> (ou a última área copiada) como pedaço solto,
    /// pronto pra mover/esticar/girar. Vai na posição da seleção atual ou no centro;
    /// se for maior que a imagem, encolhe pra caber. No modo camadas vira camada nova.
    /// Esc / Ctrl+Z desfazem a colagem.
    /// </summary>
    public bool PasteImage(SKBitmap? image = null)
    {
        var source = image ?? s_clipboard;
        if (source is null || source.Width < 1 || source.Height < 1) return false;

        var pixels = new SKBitmap(new SKImageInfo(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var c = new SKCanvas(pixels))
        {
            c.Clear(SKColors.Transparent);
            c.DrawBitmap(source, 0, 0);
        }

        float scale = Math.Min(1f, Math.Min(Bitmap.Width / (float)pixels.Width, Bitmap.Height / (float)pixels.Height));
        float w = pixels.Width * scale, h = pixels.Height * scale;
        var anchor = Selection;
        CommitSelection();

        float left = anchor is { } a ? a.Left : (Bitmap.Width - w) / 2f;
        float top = anchor is { } b ? b.Top : (Bitmap.Height - h) / 2f;
        left = Math.Clamp(left, 0, Math.Max(Bitmap.Width - w, 0));
        top = Math.Clamp(top, 0, Math.Max(Bitmap.Height - h, 0));

        if (LayerMode)
        {
            var layer = EditorLayer.FromImage(pixels, "Colado", left, top, scale);
            InsertLayer(layer, "Área colada como camada nova — arraste pra posicionar.");
            return true;
        }

        if (Tool != EditorTool.Select) Tool = EditorTool.Select;
        PushUndo();
        BeginFloating(pixels, null, SKRect.Create(left, top, w, h));
        StatusText = "Colado — arraste pra posicionar, puxe as alças pra esticar. Enter aplica.";
        return true;
    }

    /// <summary>Atalhos do painel: esticar até a tela inteira / só na largura / só na altura.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void StretchSelection(string? mode)
    {
        if (Selection is null || !LiftSelection()) return;
        var r = _floatRect;
        var full = SKRect.Create(0, 0, Bitmap.Width, Bitmap.Height);
        _floatAngle = 0;
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

    /// <summary>Campo de largura/altura do painel: redimensiona mantendo o centro
    /// quando girado, ou a partir do canto superior esquerdo sem giro.</summary>
    public void ResizeSelectionTo(float width, float height)
    {
        if (Selection is null || width < 1 || height < 1 || !LiftSelection()) return;
        var r = _floatRect;
        SetFloatingRect(_floatAngle == 0
            ? SKRect.Create(r.Left, r.Top, width, height)
            : SKRect.Create(r.MidX - width / 2, r.MidY - height / 2, width, height));
    }

    /// <summary>Desenha o pedaço flutuante (prévia), por cima de tudo.</summary>
    public void DrawFloatingSelection(SKCanvas canvas)
    {
        if (_floatPixels is { } pixels) DrawFloating(canvas, pixels);
    }

    private void DrawFloating(SKCanvas canvas, SKBitmap pixels)
    {
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
        var r = _floatRect;
        canvas.Save();
        canvas.Translate(r.MidX, r.MidY);
        if (_floatAngle != 0) canvas.RotateDegrees(_floatAngle);
        canvas.DrawBitmap(pixels, SKRect.Create(-r.Width / 2, -r.Height / 2, r.Width, r.Height), paint);
        canvas.Restore();
    }

    private void DropFloating()
    {
        _floatPixels?.Dispose();
        _floatPixels = null;
        _floatTarget = null;
        _floatAngle = 0;
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

    /// <summary>Contorno alinhado aos eixos de um retângulo girado em torno do centro.</summary>
    public static SKRect RotatedBounds(SKRect r, float degrees)
    {
        if (degrees == 0) return r;
        double rad = degrees * Math.PI / 180, cos = Math.Abs(Math.Cos(rad)), sin = Math.Abs(Math.Sin(rad));
        float w = (float)(r.Width * cos + r.Height * sin), h = (float)(r.Width * sin + r.Height * cos);
        return SKRect.Create(r.MidX - w / 2, r.MidY - h / 2, w, h);
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
