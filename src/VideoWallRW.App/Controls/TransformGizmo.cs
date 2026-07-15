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

    private static DependencyProperty RegisterD(string name, double defaultValue = 0.0) =>
        DependencyProperty.Register(name, typeof(double), typeof(TransformGizmo),
            new FrameworkPropertyMetadata(defaultValue,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));

    public double PositionX { get => (double)GetValue(PositionXProperty); set => SetValue(PositionXProperty, value); }
    public double PositionY { get => (double)GetValue(PositionYProperty); set => SetValue(PositionYProperty, value); }
    public double ScaleX { get => (double)GetValue(ScaleXProperty); set => SetValue(ScaleXProperty, value); }
    public double ScaleY { get => (double)GetValue(ScaleYProperty); set => SetValue(ScaleYProperty, value); }
    public double StageWidth { get => (double)GetValue(StageWidthProperty); set => SetValue(StageWidthProperty, value); }
    public double StageHeight { get => (double)GetValue(StageHeightProperty); set => SetValue(StageHeightProperty, value); }
    public ImageSource? Thumbnail { get => (ImageSource?)GetValue(ThumbnailProperty); set => SetValue(ThumbnailProperty, value); }

    private enum Handle { None, Move, N, S, E, W, NE, NW, SE, SW }
    private Handle _drag;
    private Point _dragStartMouse;
    private double _dragStartPosX, _dragStartPosY, _dragStartScaleX, _dragStartScaleY;

    private static readonly Brush StageBrush = Frozen(Color.FromRgb(0x17, 0x17, 0x1A));
    private static readonly Brush FallbackFill = Frozen(Color.FromArgb(0x70, 0x9A, 0xCD, 0x32));
    private static readonly Brush DimOverlay = Frozen(Color.FromArgb(0x99, 0x17, 0x17, 0x1A));
    private static readonly Pen StagePen = FrozenPen(Color.FromRgb(0x3C, 0x3C, 0x44), 1);
    private static readonly Pen RectPen = FrozenPen(Color.FromRgb(0x9A, 0xCD, 0x32), 2);
    private static readonly Pen CenterCrossPen = FrozenPen(Color.FromArgb(0xC0, 0xDF, 0xDF, 0xDF), 1.4);
    private static readonly Brush HandleBrush = Frozen(Color.FromRgb(0x9A, 0xCD, 0x32));
    private static readonly Pen HandleOutline = FrozenPen(Color.FromRgb(0x17, 0x17, 0x1A), 1);

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static Pen FrozenPen(Color c, double thickness) { var p = new Pen(Frozen(c), thickness); p.Freeze(); return p; }

    public TransformGizmo()
    {
        MinHeight = 140;
        ClipToBounds = true;
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

    private Rect InnerRect(Rect stage)
    {
        double w = stage.Width * ScaleX;
        double h = stage.Height * ScaleY;
        double cx = stage.X + stage.Width / 2 + PositionX * stage.Width;
        double cy = stage.Y + stage.Height / 2 + PositionY * stage.Height;
        return new Rect(cx - w / 2, cy - h / 2, w, h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var stage = StageRect();
        dc.DrawRectangle(StageBrush, StagePen, stage);

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
            PositionX = _dragStartPosX + dx;
            PositionY = _dragStartPosY + dy;
            return;
        }

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

        if (signX != 0) ScaleX = Math.Max(0.05, _dragStartScaleX + signX * dx * 2);
        if (signY != 0) ScaleY = Math.Max(0.05, _dragStartScaleY + signY * dy * 2);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag == Handle.None) return;
        _drag = Handle.None;
        ReleaseMouseCapture();
        e.Handled = true;
    }
}
