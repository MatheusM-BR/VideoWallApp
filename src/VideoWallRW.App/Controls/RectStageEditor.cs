using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace VideoWallRW.App.Controls;

/// <summary>
/// Editor visual de um retângulo (X/Y/W/H) dentro de um "palco" de tamanho fixo —
/// usado no Advanced Output pro recorte de entrada (contra o tamanho da composição)
/// e a posição de saída (contra o tamanho do monitor), igual ao editor de slice do
/// Resolume Arena: arrastar por dentro move, 8 alças (4 cantos + 4 arestas) redimensionam.
/// </summary>
public sealed class RectStageEditor : FrameworkElement
{
    private const double HandleSize = 9;

    public static readonly DependencyProperty StageWidthProperty =
        DependencyProperty.Register(nameof(StageWidth), typeof(double), typeof(RectStageEditor),
            new FrameworkPropertyMetadata(1920.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StageHeightProperty =
        DependencyProperty.Register(nameof(StageHeight), typeof(double), typeof(RectStageEditor),
            new FrameworkPropertyMetadata(1080.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RectXProperty = RegisterRect(nameof(RectX));
    public static readonly DependencyProperty RectYProperty = RegisterRect(nameof(RectY));
    public static readonly DependencyProperty RectWidthProperty = RegisterRect(nameof(RectWidth), 100.0);
    public static readonly DependencyProperty RectHeightProperty = RegisterRect(nameof(RectHeight), 100.0);

    private static DependencyProperty RegisterRect(string name, double defaultValue = 0.0) =>
        DependencyProperty.Register(name, typeof(double), typeof(RectStageEditor),
            new FrameworkPropertyMetadata(defaultValue,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));

    public double StageWidth { get => (double)GetValue(StageWidthProperty); set => SetValue(StageWidthProperty, value); }
    public double StageHeight { get => (double)GetValue(StageHeightProperty); set => SetValue(StageHeightProperty, value); }
    public double RectX { get => (double)GetValue(RectXProperty); set => SetValue(RectXProperty, value); }
    public double RectY { get => (double)GetValue(RectYProperty); set => SetValue(RectYProperty, value); }
    public double RectWidth { get => (double)GetValue(RectWidthProperty); set => SetValue(RectWidthProperty, value); }
    public double RectHeight { get => (double)GetValue(RectHeightProperty); set => SetValue(RectHeightProperty, value); }

    private enum Handle { None, Move, N, S, E, W, NE, NW, SE, SW }
    private Handle _drag;
    private Point _dragStartMouse;
    private double _startX, _startY, _startW, _startH;

    private static readonly Brush StageBrush = Frozen(Color.FromRgb(0x17, 0x17, 0x1A));
    private static readonly Brush RectBrush = Frozen(Color.FromArgb(0x50, 0x9A, 0xCD, 0x32));
    private static readonly Pen StagePen = FrozenPen(Color.FromRgb(0x3C, 0x3C, 0x44), 1);
    private static readonly Pen RectPen = FrozenPen(Color.FromRgb(0x9A, 0xCD, 0x32), 2);
    private static readonly Brush HandleBrush = Frozen(Color.FromRgb(0x9A, 0xCD, 0x32));

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static Pen FrozenPen(Color c, double t) { var p = new Pen(Frozen(c), t); p.Freeze(); return p; }

    public RectStageEditor()
    {
        MinHeight = 120;
        ClipToBounds = true;
    }

    /// <summary>Escala palco→tela, preservando a proporção (letterbox dentro do controle).</summary>
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

    private Rect ToScreen(double x, double y, double w, double h)
    {
        var (scale, offX, offY) = Fit();
        return new Rect(offX + x * scale, offY + y * scale, w * scale, h * scale);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var stage = ToScreen(0, 0, StageWidth, StageHeight);
        dc.DrawRectangle(StageBrush, StagePen, stage);

        var rect = ToScreen(RectX, RectY, RectWidth, RectHeight);
        dc.DrawRectangle(RectBrush, RectPen, rect);

        foreach (var (p, _) in HandlePoints(rect))
            dc.DrawRectangle(HandleBrush, null, new Rect(p.X - HandleSize / 2, p.Y - HandleSize / 2, HandleSize, HandleSize));
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
        var p = e.GetPosition(this);
        var rect = ToScreen(RectX, RectY, RectWidth, RectHeight);

        _drag = Handle.None;
        foreach (var (hp, h) in HandlePoints(rect))
        {
            if (Math.Abs(p.X - hp.X) <= HandleSize && Math.Abs(p.Y - hp.Y) <= HandleSize) { _drag = h; break; }
        }
        if (_drag == Handle.None && rect.Contains(p)) _drag = Handle.Move;
        if (_drag == Handle.None) return;

        _dragStartMouse = p;
        _startX = RectX; _startY = RectY; _startW = RectWidth; _startH = RectHeight;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag == Handle.None) return;
        var (scale, _, _) = Fit();
        if (scale <= 0) return;
        var p = e.GetPosition(this);
        double dx = (p.X - _dragStartMouse.X) / scale;
        double dy = (p.Y - _dragStartMouse.Y) / scale;

        double x = _startX, y = _startY, w = _startW, h = _startH;
        switch (_drag)
        {
            case Handle.Move: x += dx; y += dy; break;
            case Handle.N: y += dy; h -= dy; break;
            case Handle.S: h += dy; break;
            case Handle.W: x += dx; w -= dx; break;
            case Handle.E: w += dx; break;
            case Handle.NW: x += dx; y += dy; w -= dx; h -= dy; break;
            case Handle.NE: y += dy; w += dx; h -= dy; break;
            case Handle.SW: x += dx; w -= dx; h += dy; break;
            case Handle.SE: w += dx; h += dy; break;
        }

        const double minSize = 4;
        if (w < minSize) w = minSize;
        if (h < minSize) h = minSize;

        RectX = x; RectY = y; RectWidth = w; RectHeight = h;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag == Handle.None) return;
        _drag = Handle.None;
        ReleaseMouseCapture();
        e.Handled = true;
    }
}
