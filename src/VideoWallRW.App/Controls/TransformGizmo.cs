using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VideoWallRW.App.Controls;

/// <summary>
/// Painel CLIP interativo, igual ao editor de posição/escala do Resolume Arena:
/// um "palco" no formato real da composição (StageWidth/Height), com o clip
/// selecionado desenhado dentro dele (usando a miniatura real quando disponível)
/// e 8 alças (4 cantos + 4 arestas) pra redimensionar. Como o modelo de dados é
/// Position/Scale relativos ao centro (não X/Y/W/H de canto), toda alça redimensiona
/// simetricamente em torno do centro — arrastar qualquer borda "abre" pros dois lados.
/// </summary>
public sealed class TransformGizmo : FrameworkElement
{
    private const double HandleSize = 10;

    public static readonly DependencyProperty PositionXProperty = RegisterD(nameof(PositionX));
    public static readonly DependencyProperty PositionYProperty = RegisterD(nameof(PositionY));
    public static readonly DependencyProperty ScaleXProperty = RegisterD(nameof(ScaleX), 1.0);
    public static readonly DependencyProperty ScaleYProperty = RegisterD(nameof(ScaleY), 1.0);
    public static readonly DependencyProperty StageWidthProperty =
        DependencyProperty.Register(nameof(StageWidth), typeof(double), typeof(TransformGizmo),
            new FrameworkPropertyMetadata(1920.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StageHeightProperty =
        DependencyProperty.Register(nameof(StageHeight), typeof(double), typeof(TransformGizmo),
            new FrameworkPropertyMetadata(1080.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ThumbnailProperty =
        DependencyProperty.Register(nameof(Thumbnail), typeof(ImageSource), typeof(TransformGizmo),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    // Dimensões nativas do arquivo + modo de Resize (T: "transformar ainda fica
    // esticado"): sem isso o retângulo-base sempre assumia "preenche a composição
    // inteira" (Scale 1 = tela toda), que só é verdade pros modos Fill/Stretch — com
    // Resize=Original (padrão de clip novo) ou Fit, a mídia real não preenche tudo,
    // e o preview ficava esticado mesmo quando o telão de verdade não fica.
    public static readonly DependencyProperty SourceWidthProperty =
        DependencyProperty.Register(nameof(SourceWidth), typeof(double), typeof(TransformGizmo),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SourceHeightProperty =
        DependencyProperty.Register(nameof(SourceHeight), typeof(double), typeof(TransformGizmo),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ClipResizeModeProperty =
        DependencyProperty.Register(nameof(ClipResizeMode), typeof(Core.Model.ResizeMode?), typeof(TransformGizmo),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double SourceWidth { get => (double)GetValue(SourceWidthProperty); set => SetValue(SourceWidthProperty, value); }
    public double SourceHeight { get => (double)GetValue(SourceHeightProperty); set => SetValue(SourceHeightProperty, value); }
    public Core.Model.ResizeMode? ClipResizeMode
    {
        get => (Core.Model.ResizeMode?)GetValue(ClipResizeModeProperty);
        set => SetValue(ClipResizeModeProperty, value);
    }

    private static DependencyProperty RegisterD(string name, double defaultValue = 0.0) =>
        DependencyProperty.Register(name, typeof(double), typeof(TransformGizmo),
            new FrameworkPropertyMetadata(defaultValue,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));

    public double PositionX { get => (double)GetValue(PositionXProperty); set => SetCurrentValue(PositionXProperty, value); }
    public double PositionY { get => (double)GetValue(PositionYProperty); set => SetCurrentValue(PositionYProperty, value); }
    public double ScaleX { get => (double)GetValue(ScaleXProperty); set => SetCurrentValue(ScaleXProperty, value); }
    public double ScaleY { get => (double)GetValue(ScaleYProperty); set => SetCurrentValue(ScaleYProperty, value); }
    public double StageWidth { get => (double)GetValue(StageWidthProperty); set => SetValue(StageWidthProperty, value); }
    public double StageHeight { get => (double)GetValue(StageHeightProperty); set => SetValue(StageHeightProperty, value); }
    public ImageSource? Thumbnail { get => (ImageSource?)GetValue(ThumbnailProperty); set => SetValue(ThumbnailProperty, value); }

    private enum Handle { None, Move, N, S, E, W, NE, NW, SE, SW }
    private Handle _drag;
    private Point _dragStartMouse;
    private double _dragStartPosX, _dragStartPosY, _dragStartScaleX, _dragStartScaleY;

    private static readonly Brush StageBrush = Frozen(Color.FromRgb(0x17, 0x17, 0x1A));
    private static readonly Brush FallbackFill = Frozen(Color.FromArgb(0x70, 0x82, 0xBA, 0xF6));
    private static readonly Brush DimOverlay = Frozen(Color.FromArgb(0x99, 0x17, 0x17, 0x1A));
    private static readonly Pen StagePen = FrozenPen(Color.FromRgb(0x3C, 0x3C, 0x44), 1);
    private static readonly Pen RectPen = FrozenPen(Color.FromRgb(0x82, 0xBA, 0xF6), 2);
    private static readonly Pen CenterCrossPen = FrozenPen(Color.FromArgb(0xC0, 0xDF, 0xDF, 0xDF), 1.4);
    private static readonly Brush HandleBrush = Frozen(Color.FromRgb(0x82, 0xBA, 0xF6));
    private static readonly Pen HandleOutline = FrozenPen(Color.FromRgb(0x17, 0x17, 0x1A), 1);

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static Pen FrozenPen(Color c, double thickness) { var p = new Pen(Frozen(c), thickness); p.Freeze(); return p; }

    public TransformGizmo()
    {
        MinHeight = 140;
        ClipToBounds = true;
        Focusable = true;
        IsKeyboardFocusedChanged += (_, _) => InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        double pixels = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
        switch (e.Key)
        {
            case Key.Left: SetCurrentValue(PositionXProperty, Math.Clamp(PositionX - pixels / Math.Max(StageWidth, 1), -2, 2)); break;
            case Key.Right: SetCurrentValue(PositionXProperty, Math.Clamp(PositionX + pixels / Math.Max(StageWidth, 1), -2, 2)); break;
            case Key.Up: SetCurrentValue(PositionYProperty, Math.Clamp(PositionY - pixels / Math.Max(StageHeight, 1), -2, 2)); break;
            case Key.Down: SetCurrentValue(PositionYProperty, Math.Clamp(PositionY + pixels / Math.Max(StageHeight, 1), -2, 2)); break;
            case Key.Home: SetCurrentValue(PositionXProperty, 0.0); SetCurrentValue(PositionYProperty, 0.0); break;
            default: base.OnKeyDown(e); return;
        }
        e.Handled = true;
    }

    /// <summary>Encaixa o retângulo StageWidth×StageHeight dentro do controle, preservando a proporção real da composição.</summary>
    private (double Scale, double OffsetX, double OffsetY) Fit()
    {
        double pad = HandleSize;
        double availW = Math.Max(ActualWidth - pad * 2, 1);
        double availH = Math.Max(ActualHeight - pad * 2, 1);
        double scale = Math.Min(availW / Math.Max(StageWidth, 1), availH / Math.Max(StageHeight, 1));
        double offX = pad + (availW - StageWidth * scale) / 2;
        double offY = pad + (availH - StageHeight * scale) / 2;
        return (scale, offX, offY);
    }

    private Rect StageRect()
    {
        var (scale, offX, offY) = Fit();
        return new Rect(offX, offY, StageWidth * scale, StageHeight * scale);
    }

    /// <summary>Retângulo-base (antes do Position/Scale manual) dentro do palco — o
    /// mesmo que <c>CompositionRenderer.ApplyClipTransform</c> usa como ponto de
    /// partida no motor de verdade: calculado via <c>ResizeMath.Compute</c> a partir
    /// das dimensões nativas do arquivo e do Resize escolhido. Sem dados de mídia
    /// ainda (SourceWidth/Height zerados), cai no palco inteiro como antes.</summary>
    private Rect BaseRect(Rect stage)
    {
        if (SourceWidth <= 0 || SourceHeight <= 0)
            return stage;

        var mode = ClipResizeMode ?? Core.Model.ResizeMode.Fit;
        var baseComp = Core.Math.ResizeMath.Compute(mode, (float)SourceWidth, (float)SourceHeight,
            (float)StageWidth, (float)StageHeight);

        double sx = stage.Width / Math.Max(StageWidth, 1);
        double sy = stage.Height / Math.Max(StageHeight, 1);
        return new Rect(stage.X + baseComp.X * sx, stage.Y + baseComp.Y * sy, baseComp.Width * sx, baseComp.Height * sy);
    }

    private Rect InnerRect(Rect stage)
    {
        var baseRect = BaseRect(stage);
        double w = baseRect.Width * ScaleX;
        double h = baseRect.Height * ScaleY;
        double cx = baseRect.X + baseRect.Width / 2 + PositionX * stage.Width;
        double cy = baseRect.Y + baseRect.Height / 2 + PositionY * stage.Height;
        return new Rect(cx - w / 2, cy - h / 2, w, h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var stage = StageRect();
        dc.DrawRectangle(StageBrush, StagePen, stage);
        if (IsKeyboardFocused) dc.DrawRectangle(null, RectPen, new Rect(1, 1, Math.Max(0, ActualWidth - 2), Math.Max(0, ActualHeight - 2)));

        var inner = InnerRect(stage);

        dc.PushClip(new RectangleGeometry(stage));
        if (Thumbnail is { } thumb)
            dc.DrawImage(thumb, inner);
        else
            dc.DrawRectangle(FallbackFill, null, inner);
        // Escurece a área do palco fora do clip, pra deixar claro o que é composição x clip.
        var outsideGeo = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(stage), new RectangleGeometry(inner));
        dc.DrawGeometry(DimOverlay, null, outsideGeo);
        dc.Pop();

        dc.DrawRectangle(null, RectPen, inner);

        var center = new Point(inner.X + inner.Width / 2, inner.Y + inner.Height / 2);
        dc.DrawLine(CenterCrossPen, new Point(center.X - 7, center.Y), new Point(center.X + 7, center.Y));
        dc.DrawLine(CenterCrossPen, new Point(center.X, center.Y - 7), new Point(center.X, center.Y + 7));

        foreach (var (p, _) in HandlePoints(inner))
            dc.DrawRectangle(HandleBrush, HandleOutline, new Rect(p.X - HandleSize / 2, p.Y - HandleSize / 2, HandleSize, HandleSize));
    }

    private static IEnumerable<(Point Point, Handle Handle)> HandlePoints(Rect r)
    {
        yield return (new Point(r.Left, r.Top), Handle.NW);
        yield return (new Point(r.Right, r.Top), Handle.NE);
        yield return (new Point(r.Left, r.Bottom), Handle.SW);
        yield return (new Point(r.Right, r.Bottom), Handle.SE);
        yield return (new Point((r.Left + r.Right) / 2, r.Top), Handle.N);
        yield return (new Point((r.Left + r.Right) / 2, r.Bottom), Handle.S);
        yield return (new Point(r.Left, (r.Top + r.Bottom) / 2), Handle.W);
        yield return (new Point(r.Right, (r.Top + r.Bottom) / 2), Handle.E);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var stage = StageRect();
        var inner = InnerRect(stage);
        var p = e.GetPosition(this);

        _drag = Handle.None;
        foreach (var (hp, h) in HandlePoints(inner))
        {
            if (Math.Abs(p.X - hp.X) <= HandleSize && Math.Abs(p.Y - hp.Y) <= HandleSize) { _drag = h; break; }
        }
        if (_drag == Handle.None && inner.Contains(p)) _drag = Handle.Move;
        if (_drag == Handle.None) return;

        Focus();
        _dragStartMouse = p;
        _dragStartPosX = PositionX; _dragStartPosY = PositionY;
        _dragStartScaleX = ScaleX; _dragStartScaleY = ScaleY;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag == Handle.None) return;
        var stage = StageRect();
        var p = e.GetPosition(this);
        double dx = (p.X - _dragStartMouse.X) / Math.Max(stage.Width, 1);
        double dy = (p.Y - _dragStartMouse.Y) / Math.Max(stage.Height, 1);

        if (_drag == Handle.Move)
        {
            PositionX = Math.Clamp(_dragStartPosX + dx, -2, 2);
            PositionY = Math.Clamp(_dragStartPosY + dy, -2, 2);
            return;
        }

        // ScaleX/Y multiplicam BaseRect (não o palco inteiro) desde o fix do "transformar
        // esticado" — então o delta de escala tem que ser fração do BaseRect, senão arrastar
        // a alça em Fit/Original (BaseRect bem menor que o palco) move a borda bem menos do
        // que o mouse andou, "descolando" a alça do cursor.
        var baseRect = BaseRect(stage);
        double dxScale = (p.X - _dragStartMouse.X) / Math.Max(baseRect.Width, 1);
        double dyScale = (p.Y - _dragStartMouse.Y) / Math.Max(baseRect.Height, 1);

        // Escala simétrica em torno do centro: arrastar uma borda pra fora (na direção
        // dela) aumenta o tamanho nos dois lados ao mesmo tempo — por isso dx/dy contam em dobro.
        (int signX, int signY) = _drag switch
        {
            Handle.E => (1, 0), Handle.W => (-1, 0),
            Handle.S => (0, 1), Handle.N => (0, -1),
            Handle.NE => (1, -1), Handle.NW => (-1, -1),
            Handle.SE => (1, 1), Handle.SW => (-1, 1),
            _ => (0, 0),
        };

        double newX = signX != 0 ? _dragStartScaleX + signX * dxScale * 2 : _dragStartScaleX;
        double newY = signY != 0 ? _dragStartScaleY + signY * dyScale * 2 : _dragStartScaleY;

        // CANTO mantém a PROPORÇÃO (padrão do Resolume e de qualquer ferramenta de
        // design): os dois eixos crescem pelo mesmo fator, então a mídia não deforma.
        // Segurar SHIFT solta a proporção pra esticar de propósito. As ALÇAS DE ARESTA
        // continuam mexendo num eixo só, que é justamente pra deformar/ajustar um lado.
        bool isCorner = signX != 0 && signY != 0;
        bool freeAspect = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (isCorner && !freeAspect && _dragStartScaleX > 0.0001 && _dragStartScaleY > 0.0001)
        {
            double ratioX = newX / _dragStartScaleX;
            double ratioY = newY / _dragStartScaleY;
            // Segue o eixo que o operador puxou mais — o outro acompanha na mesma razão.
            double r = Math.Abs(ratioX - 1) >= Math.Abs(ratioY - 1) ? ratioX : ratioY;
            newX = _dragStartScaleX * r;
            newY = _dragStartScaleY * r;
        }

        if (signX != 0 || isCorner) ScaleX = Math.Clamp(newX, 0.05, 8);
        if (signY != 0 || isCorner) ScaleY = Math.Clamp(newY, 0.05, 8);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag == Handle.None) return;
        _drag = Handle.None;
        ReleaseMouseCapture();
        e.Handled = true;
    }
}
