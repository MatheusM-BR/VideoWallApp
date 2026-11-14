using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VideoWallRW.App.ViewModels;

namespace VideoWallRW.App.Controls;

/// <summary>
/// Mapa de telas (T27 — "cenário"): mostra TODAS as telas configuradas de uma vez, cada
/// uma como um retângulo nomeado no espaço da composição (o retângulo é o InputRect da
/// primeira slice da tela) — dá pro operador ver de relance "tela vertical na esquerda,
/// horizontal no meio, horizontal na direita" e reorganizar arrastando, em vez de editar
/// tela por tela às cegas via campos numéricos. Clique seleciona a tela (sincroniza com
/// a lista já existente); arrastar por dentro move, alças nos cantos/arestas redimensionam.
/// </summary>
public sealed class MultiRectStageEditor : FrameworkElement
{
    private const double HandleSize = 9;

    public static readonly DependencyProperty StageWidthProperty =
        DependencyProperty.Register(nameof(StageWidth), typeof(double), typeof(MultiRectStageEditor),
            new FrameworkPropertyMetadata(1920.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StageHeightProperty =
        DependencyProperty.Register(nameof(StageHeight), typeof(double), typeof(MultiRectStageEditor),
            new FrameworkPropertyMetadata(1080.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ScreensProperty =
        DependencyProperty.Register(nameof(Screens), typeof(System.Collections.IEnumerable), typeof(MultiRectStageEditor),
            new FrameworkPropertyMetadata(null, OnScreensChanged));

    public static readonly DependencyProperty SelectedScreenProperty =
        DependencyProperty.Register(nameof(SelectedScreen), typeof(ScreenViewModel), typeof(MultiRectStageEditor),
            new FrameworkPropertyMetadata(null,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));

    public double StageWidth { get => (double)GetValue(StageWidthProperty); set => SetValue(StageWidthProperty, value); }
    public double StageHeight { get => (double)GetValue(StageHeightProperty); set => SetValue(StageHeightProperty, value); }
    public System.Collections.IEnumerable? Screens
    {
        get => (System.Collections.IEnumerable?)GetValue(ScreensProperty);
        set => SetValue(ScreensProperty, value);
    }
    public ScreenViewModel? SelectedScreen
    {
        get => (ScreenViewModel?)GetValue(SelectedScreenProperty);
        set => SetValue(SelectedScreenProperty, value);
    }

    private enum Handle { None, Move, N, S, E, W, NE, NW, SE, SW }
    private Handle _drag;
    private ScreenViewModel? _dragScreen;
    private Point _dragStartMouse;
    private double _startX, _startY, _startW, _startH;

    private static readonly Brush StageBrush = Frozen(Color.FromRgb(0x17, 0x17, 0x1A));
    private static readonly Brush RectBrush = Frozen(Color.FromArgb(0x45, 0x82, 0xBA, 0xF6));
    private static readonly Brush RectBrushSelected = Frozen(Color.FromArgb(0x70, 0x82, 0xBA, 0xF6));
    private static readonly Pen StagePen = FrozenPen(Color.FromRgb(0x3C, 0x3C, 0x44), 1);
    private static readonly Pen RectPen = FrozenPen(Color.FromRgb(0x6B, 0x8E, 0x23), 1.5);
    private static readonly Pen RectPenSelected = FrozenPen(Color.FromRgb(0x82, 0xBA, 0xF6), 2.5);
    private static readonly Brush HandleBrush = Frozen(Color.FromRgb(0x82, 0xBA, 0xF6));
    private static readonly Brush LabelBrush = Frozen(Color.FromRgb(0xDF, 0xDF, 0xDF));

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static Pen FrozenPen(Color c, double t) { var p = new Pen(Frozen(c), t); p.Freeze(); return p; }

    public MultiRectStageEditor()
    {
        MinHeight = 160;
        ClipToBounds = true;
    }

    // ── Mantém o mapa atualizado quando algo edita as telas/slices por fora (os
    // campos numéricos do editor de tela selecionada continuam existindo). ──────
    private static void OnScreensChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (MultiRectStageEditor)d;
        self.Unsubscribe(e.OldValue as System.Collections.IEnumerable);
        self.Subscribe(e.NewValue as System.Collections.IEnumerable);
        self.InvalidateVisual();
    }

    private void Subscribe(System.Collections.IEnumerable? screens)
    {
        if (screens is INotifyCollectionChanged incc) incc.CollectionChanged += OnScreensCollectionChanged;
        if (screens is null) return;
        foreach (var obj in screens)
            if (obj is ScreenViewModel s) SubscribeScreen(s);
    }

    private void Unsubscribe(System.Collections.IEnumerable? screens)
    {
        if (screens is INotifyCollectionChanged incc) incc.CollectionChanged -= OnScreensCollectionChanged;
        if (screens is null) return;
        foreach (var obj in screens)
            if (obj is ScreenViewModel s) UnsubscribeScreen(s);
    }

    private void SubscribeScreen(ScreenViewModel s)
    {
        s.PropertyChanged += OnItemPropertyChanged;
        s.Slices.CollectionChanged += OnSlicesCollectionChanged;
        foreach (var slice in s.Slices) slice.PropertyChanged += OnItemPropertyChanged;
    }

    private void UnsubscribeScreen(ScreenViewModel s)
    {
        s.PropertyChanged -= OnItemPropertyChanged;
        s.Slices.CollectionChanged -= OnSlicesCollectionChanged;
        foreach (var slice in s.Slices) slice.PropertyChanged -= OnItemPropertyChanged;
    }

    private void OnScreensCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (var obj in e.OldItems) if (obj is ScreenViewModel s) UnsubscribeScreen(s);
        if (e.NewItems is not null)
            foreach (var obj in e.NewItems) if (obj is ScreenViewModel s) SubscribeScreen(s);
        InvalidateVisual();
    }

    private void OnSlicesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (var obj in e.OldItems) if (obj is SliceViewModel sl) sl.PropertyChanged -= OnItemPropertyChanged;
        if (e.NewItems is not null)
            foreach (var obj in e.NewItems) if (obj is SliceViewModel sl) sl.PropertyChanged += OnItemPropertyChanged;
        InvalidateVisual();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    private IEnumerable<ScreenViewModel> ScreenList =>
        Screens?.OfType<ScreenViewModel>() ?? [];

    private static SliceViewModel? PrimarySlice(ScreenViewModel s) => s.Slices.FirstOrDefault();

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

        foreach (var screen in ScreenList)
        {
            if (PrimarySlice(screen) is not { } slice) continue;
            bool selected = ReferenceEquals(screen, SelectedScreen);
            var rect = ToScreen(slice.InputX, slice.InputY, slice.InputWidth, slice.InputHeight);
            dc.DrawRectangle(selected ? RectBrushSelected : RectBrush, selected ? RectPenSelected : RectPen, rect);

            var label = new FormattedText(screen.Name, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, LabelBrush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip)
            { MaxTextWidth = Math.Max(rect.Width - 6, 1), Trimming = TextTrimming.CharacterEllipsis };
            dc.DrawText(label, new Point(rect.X + 4, rect.Y + 4));

            if (selected)
                foreach (var (p, _) in HandlePoints(rect))
                    dc.DrawRectangle(HandleBrush, null, new Rect(p.X - HandleSize / 2, p.Y - HandleSize / 2, HandleSize, HandleSize));
        }
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

        // Alças só do item já selecionado — evita ambiguidade quando telas se sobrepõem.
        if (SelectedScreen is not null && PrimarySlice(SelectedScreen) is { } selSlice)
        {
            var selRect = ToScreen(selSlice.InputX, selSlice.InputY, selSlice.InputWidth, selSlice.InputHeight);
            foreach (var (hp, h) in HandlePoints(selRect))
            {
                if (Math.Abs(p.X - hp.X) <= HandleSize && Math.Abs(p.Y - hp.Y) <= HandleSize)
                {
                    BeginDrag(h, SelectedScreen, selSlice, p);
                    return;
                }
            }
        }

        // Depois, corpo de qualquer tela (a última na lista desenha por cima —
        // percorre de trás pra frente pra selecionar a que está "no topo" visualmente).
        foreach (var screen in ScreenList.Reverse())
        {
            if (PrimarySlice(screen) is not { } slice) continue;
            var rect = ToScreen(slice.InputX, slice.InputY, slice.InputWidth, slice.InputHeight);
            if (rect.Contains(p))
            {
                SelectedScreen = screen;
                BeginDrag(Handle.Move, screen, slice, p);
                return;
            }
        }
    }

    private void BeginDrag(Handle handle, ScreenViewModel screen, SliceViewModel slice, Point mouse)
    {
        _drag = handle;
        _dragScreen = screen;
        _dragStartMouse = mouse;
        _startX = slice.InputX; _startY = slice.InputY;
        _startW = slice.InputWidth; _startH = slice.InputHeight;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag == Handle.None || _dragScreen is null || PrimarySlice(_dragScreen) is not { } slice) return;
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

        slice.InputX = x; slice.InputY = y; slice.InputWidth = w; slice.InputHeight = h;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag == Handle.None) return;
        _drag = Handle.None;
        _dragScreen = null;
        ReleaseMouseCapture();
        e.Handled = true;
    }
}
