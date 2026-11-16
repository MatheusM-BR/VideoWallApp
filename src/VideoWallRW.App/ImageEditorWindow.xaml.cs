using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using VideoWallRW.App.ViewModels;
using VideoWallRW.App.Controls;

namespace VideoWallRW.App;

public partial class ImageEditorWindow : Window
{
    private readonly ImageEditorViewModel _vm;
    private SKPoint? _dragStart;
    private SKPoint? _lastPaintPoint;
    private SKRect? _liveCropRect;
    private (SKPoint A, SKPoint B)? _liveShape;

    // Interação com o quadro de corte depois de criado: mover (arrastar por dentro)
    // ou redimensionar (arrastar um canto). Fora do quadro = começa seleção nova.
    private enum CropDragMode { None, Create, Move, Resize, ResizeEdge }

    /// <summary>Qual aresta do quadro de corte está sendo puxada (alças do meio dos lados).</summary>
    private enum CropEdge { None, Left, Right, Top, Bottom }
    private CropEdge _cropEdge;
    /// <summary>Quadro no início do arraste de aresta — os três lados não puxados ficam nele.</summary>
    private SKRect _cropFixed;
    private CropDragMode _cropMode;
    private SKPoint _cropAnchor; // Resize: canto oposto fixo · Move: offset do clique ao canto sup-esq.

    // Forma/texto: arrastar fora da camada selecionada desenha a forma (ou a caixa do
    // texto) e, ao SOLTAR o clique, ela já vira camada — sem passo de "concluir".
    // Por cima de uma camada, o mesmo arraste move/redimensiona como na ferramenta Camadas.
    private bool _creatingShape;
    private SKRect? _liveTextBox;
    private SKPoint _textBoxStart;

    // Ímãs: guias ativas no momento (coordenadas da imagem), desenhadas durante o arraste.
    private readonly List<float> _snapGuidesX = [];
    private readonly List<float> _snapGuidesY = [];

    // Guias de redimensionar a TELA (estilo Paint): direita, inferior e canto.
    private enum CanvasHandle { None, N, S, E, W, NE, NW, SE, SW }
    private CanvasHandle _canvasDrag;
    // Tamanho provisório (px da imagem) enquanto o usuário arrasta uma alça ou edita
    // os campos W/H — fica pendente até "Aplicar"; null = nenhuma alteração pendente.
    private SKPoint? _liveCanvasSize;

    private Point _chipDragStart;

    public ImageEditorWindow(ImageEditorViewModel viewModel)
    {
        // _vm ANTES do InitializeComponent: eventos de XAML podem disparar durante o parse.
        _vm = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.BitmapChanged += () => Dispatcher.Invoke(() =>
        {
            Canvas.InvalidateVisual();
            UpdateTelaFields();
        });
        viewModel.HistoryRestored += () => Dispatcher.Invoke(InitializeToolOverlay);
        Closed += (_, _) => viewModel.Dispose();
        Loaded += (_, _) => UpdateTelaFields();
        Loaded += (_, _) => InitializeToolOverlay();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ImageEditorViewModel.Tool))
                Dispatcher.Invoke(InitializeToolOverlay);
            else if (e.PropertyName == nameof(ImageEditorViewModel.SnapEnabled))
                Dispatcher.Invoke(Canvas.InvalidateVisual);
            else if (e.PropertyName == nameof(ImageEditorViewModel.SelectedPreset))
                Dispatcher.Invoke(RefreshCropForSettings);
        };
    }

    private void OnPaletteClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: string hex })
            _vm.SetColorHex(hex);
        ColorSwatchToggle.IsChecked = false; // escolheu no popup da barra superior: fecha
    }

    private void OnColorPopupClosed(object? sender, EventArgs e) => ColorSwatchToggle.IsChecked = false;

    /// <summary>Conta-gotas / balde direto da barra superior.</summary>
    private void OnQuickToolClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: EditorTool tool })
            _vm.Tool = tool;
        Canvas.InvalidateVisual();
    }

    private void OnEditorCategoryClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: EditorTool tool }) return;
        // Voltar à categoria Pincéis mantém a última ferramenta de pintura escolhida.
        if (tool != EditorTool.Brush || !_vm.IsPaintingTool)
            _vm.Tool = tool;
        Canvas.InvalidateVisual();
    }

    private void InitializeToolOverlay()
    {
        Cursor = Cursors.Arrow;
        _cropMode = CropDragMode.None;
        if (_vm.Tool == EditorTool.Crop)
        {
            // Cada entrada no recorte começa limpa. Assim, trocar para Tela/Camadas
            // encerra de verdade a operação anterior e não deixa alças invisíveis ativas.
            _liveCropRect = new SKRect(0, 0, _vm.Bitmap.Width, _vm.Bitmap.Height);
            RefreshCropForSettings();
            UpdateCropFields();
        }
        else
        {
            _liveCropRect = null;
            if (_vm.Tool == EditorTool.Canvas)
            {
                _liveCanvasSize = new SKPoint(_vm.Bitmap.Width, _vm.Bitmap.Height);
                UpdateTelaFields();
            }
        }
        _creatingShape = false;
        _liveShape = null;
        _liveTextBox = null;
        UpdateApplyCanvasState();
        Canvas.InvalidateVisual();
    }

    private void RefreshCropForSettings()
    {
        if (_vm.Tool != EditorTool.Crop || NormalizedCrop() is not { } rect) return;
        var sized = ImageSelectionGeometry.FitSize(new SKRect(0, 0, rect.Width, rect.Height), rect.Width, rect.Height,
            _vm.Bitmap.Width, _vm.Bitmap.Height, CropRatio());
        var centered = new SKRect(rect.MidX - sized.Width / 2, rect.MidY - sized.Height / 2,
            rect.MidX + sized.Width / 2, rect.MidY + sized.Height / 2);
        _liveCropRect = ImageSelectionGeometry.FitSize(centered, sized.Width, sized.Height,
            _vm.Bitmap.Width, _vm.Bitmap.Height, CropRatio());
        UpdateCropFields();
        Canvas.InvalidateVisual();
    }

    private void OnZoomInClick(object sender, RoutedEventArgs e) => ChangeZoom(1.12);
    private void OnZoomOutClick(object sender, RoutedEventArgs e) => ChangeZoom(1 / 1.12);
    private void OnFitCanvasClick(object sender, RoutedEventArgs e) => ResetZoom();

    /// <summary>Zoom mantendo parado o ponto sob o mouse (ou o centro da área).</summary>
    private void ChangeZoom(double factor, Point? anchor = null)
    {
        var at = anchor ?? new Point(Canvas.ActualWidth / 2, Canvas.ActualHeight / 2);
        var img = ToImageSpace(at);
        _viewZoom = Math.Clamp(_viewZoom * factor, MinZoom, MaxZoom);
        var (scale, _, _) = ViewTransform(Canvas.ActualWidth, Canvas.ActualHeight);
        _scrollX = FracForOffset(Canvas.ActualWidth, _vm.Bitmap.Width * scale, at.X - img.X * scale, _scrollX);
        _scrollY = FracForOffset(Canvas.ActualHeight, _vm.Bitmap.Height * scale, at.Y - img.Y * scale, _scrollY);
        ZoomLabel.Text = $"{_viewZoom:P0} do ajuste";
        Canvas.InvalidateVisual();
    }

    // ── Rolagem: barras + arrastar com a roda pressionada ───────────────────────

    /// <summary>Posição da rolagem em cada eixo, 0..1 (0,5 = centralizado). Fração e
    /// não pixels: o desenho (pixels do surface) e o mouse (unidades WPF) usam escalas
    /// diferentes em telas com DPI alto, e a fração vale igual nos dois.</summary>
    private double _scrollX = 0.5, _scrollY = 0.5;
    private Point? _panStart;
    private double _panStartX, _panStartY;
    private const double ScrollMargin = 0.03; // folga em volta da imagem, fração da área

    private static double AxisOffset(double view, double content, double frac)
    {
        double margin = view * ScrollMargin;
        double extent = content + 2 * margin;
        if (extent <= view) return (view - content) / 2;   // cabe: centraliza, sem rolagem
        return margin - Math.Clamp(frac, 0, 1) * (extent - view);
    }

    private static double FracForOffset(double view, double content, double offset, double fallback)
    {
        double margin = view * ScrollMargin;
        double range = content + 2 * margin - view;
        return range <= 0 ? fallback : Math.Clamp((margin - offset) / range, 0, 1);
    }

    private void UpdateScrollBars()
    {
        if (HScroll is null || VScroll is null) return;
        var (scale, _, _) = ViewTransform(Canvas.ActualWidth, Canvas.ActualHeight);
        Configure(HScroll, Canvas.ActualWidth, _vm.Bitmap.Width * scale, _scrollX);
        Configure(VScroll, Canvas.ActualHeight, _vm.Bitmap.Height * scale, _scrollY);

        static void Configure(System.Windows.Controls.Primitives.ScrollBar bar, double view, double content, double frac)
        {
            double range = content + 2 * view * ScrollMargin - view;
            if (range <= 0 || view <= 0) { bar.Visibility = Visibility.Collapsed; return; }
            bar.Visibility = Visibility.Visible;
            bar.Minimum = 0;
            bar.Maximum = range;
            bar.ViewportSize = view;
            bar.SmallChange = Math.Max(view / 20, 1);
            bar.LargeChange = view * 0.9;
            bar.Value = Math.Clamp(frac, 0, 1) * range;
        }
    }

    private void OnScrollBarScroll(object sender, System.Windows.Controls.Primitives.ScrollEventArgs e)
    {
        var bar = (System.Windows.Controls.Primitives.ScrollBar)sender;
        double frac = bar.Maximum > 0 ? bar.Value / bar.Maximum : 0.5;
        if (ReferenceEquals(bar, HScroll)) _scrollX = frac; else _scrollY = frac;
        Canvas.InvalidateVisual();
    }

    private void PanBy(double dx, double dy)
    {
        var (scale, _, _) = ViewTransform(Canvas.ActualWidth, Canvas.ActualHeight);
        double rangeX = _vm.Bitmap.Width * scale + 2 * Canvas.ActualWidth * ScrollMargin - Canvas.ActualWidth;
        double rangeY = _vm.Bitmap.Height * scale + 2 * Canvas.ActualHeight * ScrollMargin - Canvas.ActualHeight;
        if (rangeX > 0) _scrollX = Math.Clamp(_panStartX - dx / rangeX, 0, 1);
        if (rangeY > 0) _scrollY = Math.Clamp(_panStartY - dy / rangeY, 0, 1);
        Canvas.InvalidateVisual();
    }

    // ── Chip "arraste p/ célula": salva e inicia o drag&drop de arquivo ─────────

    private void OnDragChipDown(object sender, MouseButtonEventArgs e)
        => _chipDragStart = e.GetPosition(null);

    private void OnDragChipMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _chipDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _chipDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        string path = _vm.EnsureSaved();
        DragDrop.DoDragDrop((DependencyObject)sender,
            new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Copy);
    }

    // ── Espaço imagem ↔ tela ────────────────────────────────────────────────────

    /// <summary>Zoom manual do operador (roda do mouse). 1 = encaixe automático;
    /// menor afasta (mostra mais fundo em volta), maior aproxima.</summary>
    private double _viewZoom = 1.0;
    private const double MinZoom = 0.15, MaxZoom = 8.0;

    /// <summary>
    /// ÚNICA fonte da escala/deslocamento da visualização — usada tanto pra DESENHAR
    /// quanto pra converter clique→pixel. Ter duas contas diferentes desalinhava o
    /// mouse do que aparecia na tela.
    /// <paramref name="viewW"/>/<paramref name="viewH"/> vêm do surface no desenho e do
    /// ActualWidth/Height no hit-test (são o mesmo tamanho lógico).
    /// </summary>
    private (double Scale, double OffsetX, double OffsetY) ViewTransform(double viewW, double viewH)
    {
        var bmp = _vm.Bitmap;
        // Durante o arraste da TELA a prévia cresce além da imagem: encaixa no MAIOR
        // dos dois pra nada vazar pra fora da área visível.
        float contentW = bmp.Width, contentH = bmp.Height;
        if (_vm.Tool == EditorTool.Canvas && _liveCanvasSize is { } live)
        {
            contentW = Math.Max(contentW, live.X);
            contentH = Math.Max(contentH, live.Y);
        }
        // Folga de 6% deixa as alças da borda visíveis; _viewZoom é o zoom manual.
        double scale = Math.Min(viewW / (contentW * 1.06), viewH / (contentH * 1.06)) * _viewZoom;
        double offsetX = AxisOffset(viewW, bmp.Width * scale, _scrollX);
        double offsetY = AxisOffset(viewH, bmp.Height * scale, _scrollY);
        return (scale, offsetX, offsetY);
    }

    private double CanvasScale() => ViewTransform(Canvas.ActualWidth, Canvas.ActualHeight).Scale;

    /// <summary>Mapeia um ponto do controle (coordenadas WPF) pro espaço de pixels do
    /// bitmap, considerando o "letterbox" do desenho scale-to-fit e o zoom manual.</summary>
    private SKPoint ToImageSpace(Point p)
    {
        var (scale, offsetX, offsetY) = ViewTransform(Canvas.ActualWidth, Canvas.ActualHeight);
        return new SKPoint((float)((p.X - offsetX) / scale), (float)((p.Y - offsetY) / scale));
    }

    /// <summary>Roda do mouse: afasta/aproxima a visualização (não altera a imagem —
    /// é só enquadramento, igual ao Paint 3D). Útil pra enxergar o fundo inteiro ao
    /// aumentar a tela pra além da imagem.</summary>
    private void OnCanvasMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Shift+roda rola na horizontal; Ctrl+roda rola na vertical; roda pura dá zoom.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _panStartX = _scrollX; _panStartY = _scrollY;
            bool horizontal = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            PanBy(horizontal ? e.Delta / 2.0 : 0, horizontal ? 0 : e.Delta / 2.0);
        }
        else
        {
            double factor = e.Delta > 0 ? 1.12 : 1 / 1.12;
            ChangeZoom(factor, e.GetPosition(Canvas));
        }
        e.Handled = true;
    }

    /// <summary>Volta ao encaixe automático (100%).</summary>
    private void ResetZoom()
    {
        _viewZoom = 1.0;
        _scrollX = _scrollY = 0.5;
        ZoomLabel.Text = "100% do ajuste";
        Canvas.InvalidateVisual();
    }

    // ── Mouse na área de desenho ────────────────────────────────────────────────

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Roda pressionada (botão do meio): arrasta a visualização, em qualquer ferramenta.
        if (e.ChangedButton == MouseButton.Middle)
        {
            _panStart = e.GetPosition(Canvas);
            _panStartX = _scrollX; _panStartY = _scrollY;
            Cursor = Cursors.SizeAll;
            Canvas.CaptureMouse();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return; // botão direito não pinta nem arrasta

        var pt = ToImageSpace(e.GetPosition(Canvas));
        switch (_vm.Tool)
        {
            case EditorTool.Crop:
                BeginCropDrag(pt);
                break;
            case EditorTool.Canvas:
                BeginCanvasDrag(pt);
                break;
            case EditorTool.Layers:
                BeginLayerDrag(pt, null);
                break;
            case EditorTool.Brush:
            case EditorTool.Eraser:
                _vm.BeginStroke(pt);
                _lastPaintPoint = pt;
                break;
            case EditorTool.Eyedropper:
                _vm.PickColorAt(pt);
                break;
            case EditorTool.Bucket:
                _vm.FloodFillAt(pt);
                break;
            case EditorTool.Shape:
                if (!BeginLayerDrag(pt, EditorLayerKind.Shape, createOutside: true))
                {
                    _creatingShape = true;
                    _liveShape = (pt, pt);
                }
                break;
            case EditorTool.Text:
                if (!BeginLayerDrag(pt, EditorLayerKind.Text, createOutside: true))
                {
                    _textBoxStart = pt;
                    _liveTextBox = new SKRect(pt.X, pt.Y, pt.X, pt.Y);
                }
                break;
        }
        Canvas.CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_panStart is { } panFrom)
        {
            var now = e.GetPosition(Canvas);
            PanBy(now.X - panFrom.X, now.Y - panFrom.Y);
            return;
        }

        var pt = ToImageSpace(e.GetPosition(Canvas));

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            UpdateHandleCursor(pt);
            return;
        }

        if (_vm.Tool == EditorTool.Crop && _cropMode != CropDragMode.None)
        {
            ContinueCropDrag(pt);
            UpdateCropFields();
            Canvas.InvalidateVisual();
        }
        else if (_vm.Tool == EditorTool.Canvas && _canvasDrag != CanvasHandle.None)
        {
            ContinueCanvasDrag(pt);
            Canvas.InvalidateVisual();
        }
        else if (_layerDrag != LayerDragMode.None)
        {
            ContinueLayerDrag(pt);
            Canvas.InvalidateVisual();
        }
        else if (_vm.Tool is EditorTool.Brush or EditorTool.Eraser && _lastPaintPoint is { } last)
        {
            _vm.PaintStroke(last, pt);
            _lastPaintPoint = pt;
            Canvas.InvalidateVisual();
        }
        else if (_vm.Tool == EditorTool.Shape && _creatingShape && _liveShape is { } s)
        {
            _liveShape = (s.A, pt);
            Canvas.InvalidateVisual();
        }
        else if (_vm.Tool == EditorTool.Text && _liveTextBox is not null)
        {
            _liveTextBox = new SKRect(Math.Min(_textBoxStart.X, pt.X), Math.Min(_textBoxStart.Y, pt.Y),
                Math.Max(_textBoxStart.X, pt.X), Math.Max(_textBoxStart.Y, pt.Y));
            Canvas.InvalidateVisual();
        }
    }

    /// <summary>Cursor conforme a alça sob o ponteiro (recorte e camadas) — seta de
    /// redimensionar no canto/aresta, cruz de mover por dentro, padrão fora.</summary>
    private void UpdateHandleCursor(SKPoint pt)
    {
        float tol = (float)(12 / Math.Max(CanvasScale(), 0.01));

        if (_vm.Tool == EditorTool.Crop && _liveCropRect is { } crop)
        {
            var r = new SKRect(
                Math.Min(crop.Left, crop.Right), Math.Min(crop.Top, crop.Bottom),
                Math.Max(crop.Left, crop.Right), Math.Max(crop.Top, crop.Bottom));
            bool l = Math.Abs(pt.X - r.Left) <= tol, rt = Math.Abs(pt.X - r.Right) <= tol;
            bool t = Math.Abs(pt.Y - r.Top) <= tol, b = Math.Abs(pt.Y - r.Bottom) <= tol;
            bool inV = pt.Y > r.Top - tol && pt.Y < r.Bottom + tol;
            bool inH = pt.X > r.Left - tol && pt.X < r.Right + tol;

            Cursor =
                (l && t) || (rt && b) ? Cursors.SizeNWSE :
                (rt && t) || (l && b) ? Cursors.SizeNESW :
                (l || rt) && inV ? Cursors.SizeWE :
                (t || b) && inH ? Cursors.SizeNS :
                r.Contains(pt.X, pt.Y) ? Cursors.SizeAll :
                Cursors.Arrow;
            return;
        }

        if (_vm.Tool is EditorTool.Layers or EditorTool.Shape or EditorTool.Text && _vm.ActiveLayer is { } layer &&
            (_vm.Tool == EditorTool.Layers || layer.Kind == ToolLayerKind()))
        {
            var b2 = layer.Bounds;
            bool nwse = SKPoint.Distance(pt, new SKPoint(b2.Left, b2.Top)) <= tol
                     || SKPoint.Distance(pt, new SKPoint(b2.Right, b2.Bottom)) <= tol;
            bool nesw = SKPoint.Distance(pt, new SKPoint(b2.Right, b2.Top)) <= tol
                     || SKPoint.Distance(pt, new SKPoint(b2.Left, b2.Bottom)) <= tol;
            Cursor = nwse ? Cursors.SizeNWSE
                   : nesw ? Cursors.SizeNESW
                   : b2.Contains(pt.X, pt.Y) ? Cursors.SizeAll
                   : _vm.Tool == EditorTool.Layers ? Cursors.Arrow : Cursors.Cross;
            return;
        }

        if (_vm.Tool == EditorTool.Canvas)
        {
            _canvasDrag = HitCanvasHandle(pt);
            Cursor = _canvasDrag switch
            {
                CanvasHandle.NW or CanvasHandle.SE => Cursors.SizeNWSE,
                CanvasHandle.NE or CanvasHandle.SW => Cursors.SizeNESW,
                CanvasHandle.W or CanvasHandle.E => Cursors.SizeWE,
                CanvasHandle.N or CanvasHandle.S => Cursors.SizeNS,
                _ => Cursors.Arrow,
            };
            _canvasDrag = CanvasHandle.None;
            return;
        }

        Cursor = _vm.Tool is EditorTool.Shape or EditorTool.Text ? Cursors.Cross : Cursors.Arrow;
    }

    private EditorLayerKind? ToolLayerKind() => _vm.Tool switch
    {
        EditorTool.Shape => EditorLayerKind.Shape,
        EditorTool.Text => EditorLayerKind.Text,
        _ => null,
    };

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_panStart is not null)
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            _panStart = null;
            Cursor = Cursors.Arrow;
            Canvas.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;
        Canvas.ReleaseMouseCapture();

        // Forma: soltou o clique, já vira camada (minúscula = clique acidental, descarta).
        if (_vm.Tool == EditorTool.Shape && _creatingShape && _liveShape is { } newShape)
            _vm.AddShapeLayer(newShape.A, newShape.B);
        _creatingShape = false;
        _liveShape = null;

        // Texto: caixa arrastada define o tamanho; clique simples usa o campo Tamanho.
        if (_vm.Tool == EditorTool.Text && _liveTextBox is { } box)
        {
            if (box.Height >= 6 / Math.Max(CanvasScale(), 0.01))
                _vm.AddTextLayer(box);
            else
                _vm.AddTextLayer(new SKPoint(box.Left, box.Top));
        }
        _liveTextBox = null;
        _snapGuidesX.Clear();
        _snapGuidesY.Clear();

        // A expansão fica em prévia até o botão Aplicar. Isso evita uma mudança
        // destrutiva quando o operador apenas solta uma alça para conferir o tamanho.
        if (_vm.Tool == EditorTool.Canvas && _canvasDrag != CanvasHandle.None)
        {
            _canvasDrag = CanvasHandle.None;
            Canvas.InvalidateVisual();
        }

        _layerDrag = LayerDragMode.None;
        Cursor = Cursors.Arrow;
        _dragStart = null;
        _lastPaintPoint = null;
        _cropMode = CropDragMode.None; // o quadro de corte permanece, editável
        Canvas.InvalidateVisual();
    }

}
