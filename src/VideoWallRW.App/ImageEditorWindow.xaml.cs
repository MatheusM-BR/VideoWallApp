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

    // Mesma interação do quadro de corte, aplicada à forma: depois de desenhada ela
    // fica pendente (editável) até "Concluir forma" — arrastar um canto redimensiona,
    // arrastar por dentro move, fora dela começa uma forma nova.
    private enum ShapeDragMode { None, Create, Move, Resize }
    private ShapeDragMode _shapeMode;
    private SKPoint _shapeAnchor;

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
        Closed += (_, _) => viewModel.Dispose();
        Loaded += (_, _) => UpdateTelaFields();
        Loaded += (_, _) => InitializeToolOverlay();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ImageEditorViewModel.Tool))
                Dispatcher.Invoke(InitializeToolOverlay);
            else if (e.PropertyName == nameof(ImageEditorViewModel.SelectedPreset))
                Dispatcher.Invoke(RefreshCropForSettings);
        };
    }

    private void OnPaletteClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: string hex })
            _vm.SetColorHex(hex);
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

    private void ChangeZoom(double factor)
    {
        _viewZoom = Math.Clamp(_viewZoom * factor, MinZoom, MaxZoom);
        ZoomLabel.Text = $"{_viewZoom:P0} do ajuste";
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
        double offsetX = (viewW - bmp.Width * scale) / 2;
        double offsetY = (viewH - bmp.Height * scale) / 2;
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
        double factor = e.Delta > 0 ? 1.12 : 1 / 1.12;
        ChangeZoom(factor);
        e.Handled = true;
    }

    /// <summary>Volta ao encaixe automático (100%).</summary>
    private void ResetZoom()
    {
        _viewZoom = 1.0;
        ZoomLabel.Text = "100% do ajuste";
        Canvas.InvalidateVisual();
    }

    // ── Mouse na área de desenho ────────────────────────────────────────────────

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
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
                BeginLayerDrag(pt);
                break;
            case EditorTool.Brush:
            case EditorTool.Eraser:
                _vm.BeginStroke();
                _lastPaintPoint = pt;
                break;
            case EditorTool.Eyedropper:
                _vm.PickColorAt(pt);
                break;
            case EditorTool.Bucket:
                _vm.FloodFillAt(pt);
                break;
            case EditorTool.Shape:
                BeginShapeDrag(pt);
                break;
            case EditorTool.Text:
                _vm.ApplyText(pt);
                break;
        }
        Canvas.CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
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
        else if (_vm.Tool == EditorTool.Layers && _layerDrag != LayerDragMode.None)
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
        else if (_vm.Tool == EditorTool.Shape && _shapeMode != ShapeDragMode.None)
        {
            ContinueShapeDrag(pt);
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

        if (_vm.Tool == EditorTool.Layers && _vm.ActiveLayer is { } layer)
        {
            var b2 = layer.Bounds;
            bool nwse = SKPoint.Distance(pt, new SKPoint(b2.Left, b2.Top)) <= tol
                     || SKPoint.Distance(pt, new SKPoint(b2.Right, b2.Bottom)) <= tol;
            bool nesw = SKPoint.Distance(pt, new SKPoint(b2.Right, b2.Top)) <= tol
                     || SKPoint.Distance(pt, new SKPoint(b2.Left, b2.Bottom)) <= tol;
            Cursor = nwse ? Cursors.SizeNWSE
                   : nesw ? Cursors.SizeNESW
                   : b2.Contains(pt.X, pt.Y) ? Cursors.SizeAll
                   : Cursors.Arrow;
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

        Cursor = Cursors.Arrow;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        Canvas.ReleaseMouseCapture();

        // A forma fica pendente (editável — mover/redimensionar) até "Concluir forma",
        // igual ao quadro de corte; descarta só se ficou minúscula (clique acidental).
        if (_vm.Tool == EditorTool.Shape && _shapeMode == ShapeDragMode.Create && _liveShape is { } newShape &&
            Math.Abs(newShape.B.X - newShape.A.X) <= 2 && Math.Abs(newShape.B.Y - newShape.A.Y) <= 2)
        {
            _liveShape = null;
        }
        _shapeMode = ShapeDragMode.None;

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

    // ── Quadro de corte: criar, mover e redimensionar ──────────────────────────

    private SKRect? NormalizedCrop() => _liveCropRect is { } r
        ? new SKRect(Math.Min(r.Left, r.Right), Math.Min(r.Top, r.Bottom),
                     Math.Max(r.Left, r.Right), Math.Max(r.Top, r.Bottom))
        : null;

    private void BeginCropDrag(SKPoint pt)
    {
        pt = new SKPoint(Math.Clamp(pt.X, 0, _vm.Bitmap.Width), Math.Clamp(pt.Y, 0, _vm.Bitmap.Height));
        float tolerance = (float)(14 / Math.Max(CanvasScale(), 0.01)); // ~14px de tela

        if (NormalizedCrop() is { } rect)
        {
            SKPoint[] corners =
            [
                new(rect.Left, rect.Top), new(rect.Right, rect.Top),
                new(rect.Left, rect.Bottom), new(rect.Right, rect.Bottom),
            ];
            for (int i = 0; i < corners.Length; i++)
            {
                if (SKPoint.Distance(pt, corners[i]) <= tolerance)
                {
                    _cropMode = CropDragMode.Resize;
                    _cropAnchor = corners[3 - i]; // canto oposto
                    ContinueCropDrag(pt);
                    return;
                }
            }

            // ARESTAS (as 4 alças do meio dos lados, como no Paint 3D): mexem em UM
            // lado só, mantendo os outros três parados. Sem isto, as alças de aresta
            // apareciam desenhadas mas não pegavam — afordância mentindo pro operador.
            bool nearLeft = Math.Abs(pt.X - rect.Left) <= tolerance;
            bool nearRight = Math.Abs(pt.X - rect.Right) <= tolerance;
            bool nearTop = Math.Abs(pt.Y - rect.Top) <= tolerance;
            bool nearBottom = Math.Abs(pt.Y - rect.Bottom) <= tolerance;
            bool insideV = pt.Y > rect.Top - tolerance && pt.Y < rect.Bottom + tolerance;
            bool insideH = pt.X > rect.Left - tolerance && pt.X < rect.Right + tolerance;

            _cropEdge = (nearLeft, nearRight, nearTop, nearBottom) switch
            {
                (true, false, false, false) when insideV => CropEdge.Left,
                (false, true, false, false) when insideV => CropEdge.Right,
                (false, false, true, false) when insideH => CropEdge.Top,
                (false, false, false, true) when insideH => CropEdge.Bottom,
                _ => CropEdge.None,
            };
            if (_cropEdge != CropEdge.None)
            {
                _cropMode = CropDragMode.ResizeEdge;
                _cropFixed = rect;   // os outros três lados ficam onde estão
                ContinueCropDrag(pt);
                return;
            }

            if (rect.Contains(pt.X, pt.Y))
            {
                _cropMode = CropDragMode.Move;
                _cropAnchor = new SKPoint(pt.X - rect.Left, pt.Y - rect.Top);
                return;
            }
        }

        _cropMode = CropDragMode.Create;
        _dragStart = pt;
        _liveCropRect = new SKRect(pt.X, pt.Y, pt.X, pt.Y);
    }

    private void ContinueCropDrag(SKPoint pt)
    {
        switch (_cropMode)
        {
            case CropDragMode.Create when _dragStart is { } start:
                _liveCropRect = ImageSelectionGeometry.FromCorner(start, pt,
                    _vm.Bitmap.Width, _vm.Bitmap.Height, CropRatio());
                break;

            case CropDragMode.Resize:
                _liveCropRect = ImageSelectionGeometry.FromCorner(_cropAnchor, pt,
                    _vm.Bitmap.Width, _vm.Bitmap.Height, CropRatio());
                break;

            case CropDragMode.ResizeEdge:
            {
                _liveCropRect = ImageSelectionGeometry.ResizeEdge(_cropFixed, _cropEdge.ToString(), pt,
                    _vm.Bitmap.Width, _vm.Bitmap.Height, CropRatio());
                break;
            }

            case CropDragMode.Move when NormalizedCrop() is { } rect:
                var bmp = _vm.Bitmap;
                float left = Math.Clamp(pt.X - _cropAnchor.X, 0, Math.Max(bmp.Width - rect.Width, 0));
                float top = Math.Clamp(pt.Y - _cropAnchor.Y, 0, Math.Max(bmp.Height - rect.Height, 0));
                _liveCropRect = new SKRect(left, top, left + rect.Width, top + rect.Height);
                break;
        }
    }

    private double? CropRatio()
        => _vm.SelectedPreset?.Ratio is > 0 ? _vm.SelectedPreset.Ratio : null;

    private float HeightFor(float w, float h)
        => _vm.LockAspect && _vm.SelectedPreset?.Ratio is { } ratio
            ? Math.Sign(h == 0 ? 1 : h) * Math.Abs(w) / (float)ratio
            : h;

    private void UpdateCropFields()
    {
        if (NormalizedCrop() is { } rect)
        {
            CropW.Text = ((int)rect.Width).ToString(CultureInfo.InvariantCulture);
            CropH.Text = ((int)rect.Height).ToString(CultureInfo.InvariantCulture);
        }
    }

    private void OnCropFieldChanged(object sender, RoutedEventArgs e)
    {
        // Editar W/H redimensiona a seleção ancorada no canto superior esquerdo.
        if (NormalizedCrop() is not { } rect) return;
        if (!int.TryParse(CropW.Text, out int w) || w <= 0 ||
            !int.TryParse(CropH.Text, out int h) || h <= 0) return;

        bool heightEdited = ReferenceEquals(sender, CropH);
        _liveCropRect = ImageSelectionGeometry.FitSize(rect, w, h, _vm.Bitmap.Width, _vm.Bitmap.Height,
            CropRatio(), heightEdited);
        UpdateCropFields();
        Canvas.InvalidateVisual();
    }

    /// <summary>
    /// Teclado do editor, no padrão do Paint 3D: ENTER aplica o recorte pendente,
    /// ESC descarta o quadro. Só age quando a ferramenta de recorte está ativa e há um
    /// quadro na tela — fora disso a tecla segue o caminho normal (campos de texto etc.).
    /// </summary>
    // ── Camadas: adicionar, mover e redimensionar ─────────────────────────────

    private enum LayerDragMode { None, Move, Scale }
    private LayerDragMode _layerDrag;
    private SKPoint _layerDragStart;
    private SKPoint _layerOriginPos;
    private float _layerOriginScale;
    private LayerCorner _layerGrip;

    private void OnAddLayer(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Adicionar camada",
            Filter = "Imagens|*.png;*.jpg;*.jpeg;*.bmp|Todos os arquivos|*.*",
            InitialDirectory = AppPaths.EnsureMediaRoot(),
        };
        if (dlg.ShowDialog() == true)
        {
            _vm.AddImageLayer(dlg.FileName);
            Canvas.InvalidateVisual();
        }
    }

    /// <summary>Pega a camada ativa: canto = redimensiona (proporção mantida),
    /// dentro = move. Clicar fora seleciona outra camada que esteja sob o cursor.</summary>
    private void BeginLayerDrag(SKPoint pt)
    {
        _layerDrag = LayerDragMode.None;
        if (_vm.ActiveLayer is null && _vm.Layers.Count > 0)
            _vm.ActiveLayer = _vm.Layers.LastOrDefault(l => l.Visible && l.Bounds.Contains(pt.X, pt.Y));
        if (_vm.ActiveLayer is not { } layer) return;

        float tol = (float)(12 / Math.Max(CanvasScale(), 0.01));
        var b = layer.Bounds;

        // Qual das quatro alças foi pega (a mais próxima dentro da tolerância) — cada
        // canto ancora no oposto, então precisa saber QUAL, não só "perto de um canto".
        LayerCorner? grip = null;
        float best = tol;
        foreach (var (corner, at) in new (LayerCorner, SKPoint)[]
        {
            (LayerCorner.TopLeft, new SKPoint(b.Left, b.Top)),
            (LayerCorner.TopRight, new SKPoint(b.Right, b.Top)),
            (LayerCorner.BottomLeft, new SKPoint(b.Left, b.Bottom)),
            (LayerCorner.BottomRight, new SKPoint(b.Right, b.Bottom)),
        })
        {
            float d = SKPoint.Distance(pt, at);
            if (d <= best) { best = d; grip = corner; }
        }

        if (grip is { } g) { _layerDrag = LayerDragMode.Scale; _layerGrip = g; }
        else if (b.Contains(pt.X, pt.Y)) _layerDrag = LayerDragMode.Move;
        else
        {
            // Clique fora: seleciona a camada visível mais alta sob o cursor.
            var hit = _vm.Layers.LastOrDefault(l => l.Visible && l.Bounds.Contains(pt.X, pt.Y));
            if (hit is not null) { _vm.ActiveLayer = hit; _layerDrag = LayerDragMode.Move; layer = hit; }
            else { _vm.ActiveLayer = null; Canvas.InvalidateVisual(); return; }
        }

        _layerDragStart = pt;
        _layerOriginPos = new SKPoint(layer.X, layer.Y);
        _layerOriginScale = layer.Scale;
        _vm.BeginLayerTransform();
    }

    private void ContinueLayerDrag(SKPoint pt)
    {
        if (_vm.ActiveLayer is not { } layer || _layerDrag == LayerDragMode.None) return;

        if (_layerDrag == LayerDragMode.Move)
        {
            float x = _layerOriginPos.X + (pt.X - _layerDragStart.X);
            float y = _layerOriginPos.Y + (pt.Y - _layerDragStart.Y);
            // Mantém ao menos uma faixa da camada acessível para ela nunca se perder fora da tela.
            float visibleX = Math.Min(24 / (float)Math.Max(CanvasScale(), .01), layer.Bounds.Width);
            float visibleY = Math.Min(24 / (float)Math.Max(CanvasScale(), .01), layer.Bounds.Height);
            layer.X = Math.Clamp(x, -layer.Bounds.Width + visibleX, _vm.Bitmap.Width - visibleX);
            layer.Y = Math.Clamp(y, -layer.Bounds.Height + visibleY, _vm.Bitmap.Height - visibleY);
        }
        else
        {
            // Escala UNIFORME com o canto OPOSTO parado (ver EditorLayer.ResizeFromCorner).
            layer.ResizeFromCorner(_layerGrip, _layerOriginPos, _layerOriginScale, pt);
        }
    }

    private void OnEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;
        if (_vm.Tool == EditorTool.Crop && NormalizedCrop() is { } crop)
        {
            if (e.Key == Key.Enter) OnApplyCrop(sender, e);
            else if (e.Key == Key.Escape) OnCropCancel(sender, e);
            else if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
            {
                float step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
                float dx = e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0;
                float dy = e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0;
                float left = Math.Clamp(crop.Left + dx, 0, _vm.Bitmap.Width - crop.Width);
                float top = Math.Clamp(crop.Top + dy, 0, _vm.Bitmap.Height - crop.Height);
                _liveCropRect = new SKRect(left, top, left + crop.Width, top + crop.Height);
                UpdateCropFields();
                Canvas.InvalidateVisual();
            }
            else return;
            e.Handled = true;
        }
        else if (_vm.Tool == EditorTool.Layers && _vm.ActiveLayer is { } layer &&
                 e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            float step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
            layer.X += e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0;
            layer.Y += e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0;
            Canvas.InvalidateVisual();
            e.Handled = true;
        }
    }

    private void OnSelectBaseLayer(object sender, RoutedEventArgs e)
    {
        _vm.ActiveLayer = null;
        Canvas.InvalidateVisual();
    }

    private void OnCropCancel(object sender, RoutedEventArgs e)
    {
        _liveCropRect = new SKRect(0, 0, _vm.Bitmap.Width, _vm.Bitmap.Height);
        UpdateCropFields();
        Canvas.InvalidateVisual();
    }

    private void OnSelectFullCrop(object sender, RoutedEventArgs e) => OnCropCancel(sender, e);

    private void OnApplyCrop(object sender, RoutedEventArgs e)
    {
        if (NormalizedCrop() is { } rect)
        {
            _vm.ApplyCrop(rect);
            _liveCropRect = null;
            CropW.Text = "";
            CropH.Text = "";
            InitializeToolOverlay();
        }
    }

    // ── Forma: criar, mover e redimensionar antes de concluir ──────────────────

    private static SKRect NormalizedShape((SKPoint A, SKPoint B) s) => new(
        Math.Min(s.A.X, s.B.X), Math.Min(s.A.Y, s.B.Y),
        Math.Max(s.A.X, s.B.X), Math.Max(s.A.Y, s.B.Y));

    private void BeginShapeDrag(SKPoint pt)
    {
        float tolerance = (float)(14 / Math.Max(CanvasScale(), 0.01));

        if (_liveShape is { } shape)
        {
            var rect = NormalizedShape(shape);
            SKPoint[] corners =
            [
                new(rect.Left, rect.Top), new(rect.Right, rect.Top),
                new(rect.Left, rect.Bottom), new(rect.Right, rect.Bottom),
            ];
            for (int i = 0; i < corners.Length; i++)
            {
                if (SKPoint.Distance(pt, corners[i]) <= tolerance)
                {
                    _shapeMode = ShapeDragMode.Resize;
                    _shapeAnchor = corners[3 - i]; // canto oposto
                    return;
                }
            }

            if (rect.Contains(pt.X, pt.Y))
            {
                _shapeMode = ShapeDragMode.Move;
                _shapeAnchor = new SKPoint(pt.X - rect.Left, pt.Y - rect.Top);
                return;
            }
        }

        _shapeMode = ShapeDragMode.Create;
        _liveShape = (pt, pt);
    }

    private void ContinueShapeDrag(SKPoint pt)
    {
        switch (_shapeMode)
        {
            case ShapeDragMode.Create when _liveShape is { } s:
                _liveShape = (s.A, pt);
                break;

            case ShapeDragMode.Resize:
                _liveShape = (_shapeAnchor, pt);
                break;

            case ShapeDragMode.Move when _liveShape is { } s:
                var rect = NormalizedShape(s);
                var bmp = _vm.Bitmap;
                float left = Math.Clamp(pt.X - _shapeAnchor.X, 0, Math.Max(bmp.Width - rect.Width, 0));
                float top = Math.Clamp(pt.Y - _shapeAnchor.Y, 0, Math.Max(bmp.Height - rect.Height, 0));
                _liveShape = (new SKPoint(left, top), new SKPoint(left + rect.Width, top + rect.Height));
                break;
        }
    }

    private void OnCancelShape(object sender, RoutedEventArgs e)
    {
        _liveShape = null;
        Canvas.InvalidateVisual();
    }

    private void OnApplyShape(object sender, RoutedEventArgs e)
    {
        if (_liveShape is { } shape)
        {
            _vm.ApplyShape(shape.A, shape.B);
            _liveShape = null;
            Canvas.InvalidateVisual();
        }
    }

    // ── Tela: campos + guias de arrastar (direita, inferior, canto) ─────────────

    private bool _updatingTela;

    /// <summary>Em porcentagem, os campos são relativos a este tamanho (o que a tela
    /// tinha quando o modo foi ligado / no último Aplicar).</summary>
    private int _pctBaseW = 1, _pctBaseH = 1;
    private bool PercentMode => TelaUnit is not null && TelaUnit.SelectedIndex == 1;

    private void UpdateTelaFields()
    {
        _updatingTela = true;
        _pctBaseW = Math.Max(_vm.Bitmap.Width, 1);
        _pctBaseH = Math.Max(_vm.Bitmap.Height, 1);
        if (PercentMode)
        {
            TelaW.Text = "100";
            TelaH.Text = "100";
        }
        else
        {
            TelaW.Text = _vm.Bitmap.Width.ToString(CultureInfo.InvariantCulture);
            TelaH.Text = _vm.Bitmap.Height.ToString(CultureInfo.InvariantCulture);
        }
        _updatingTela = false;
    }

    private void OnTelaUnitChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdateTelaFields();   // troca de unidade só reescreve os campos, não redimensiona
    }

    /// <summary>
    /// Digitar apenas ESPELHA o outro campo quando a proporção está travada — NÃO
    /// aplica. Antes isto redimensionava a cada tecla: digitar "1000" destruía a arte
    /// em 1px, depois 10px, depois 100px, empilhando 4 níveis de desfazer. Agora o
    /// redimensionamento acontece só no ENTER ou ao sair do campo, como no Paint 3D.
    /// </summary>
    private void OnTelaFieldChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingTela || !_vm.LockAspect) return;

        double aspect = _vm.Bitmap.Height > 0 ? (double)_vm.Bitmap.Width / _vm.Bitmap.Height : 1;
        _updatingTela = true;
        if (ReferenceEquals(sender, TelaW) && double.TryParse(TelaW.Text, out double w) && w > 0)
            TelaH.Text = PercentMode
                ? TelaW.Text                                        // mesma % nos dois eixos
                : Math.Max((int)Math.Round(w / aspect), 1).ToString(CultureInfo.InvariantCulture);
        else if (ReferenceEquals(sender, TelaH) && double.TryParse(TelaH.Text, out double h) && h > 0)
            TelaW.Text = PercentMode
                ? TelaH.Text
                : Math.Max((int)Math.Round(h * aspect), 1).ToString(CultureInfo.InvariantCulture);
        _updatingTela = false;
    }

    private void OnTelaKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyTelaFromFields();
        e.Handled = true;
    }

    private void OnTelaCommit(object sender, RoutedEventArgs e) => ApplyTelaFromFields();

    private void OnApplyCanvas(object sender, RoutedEventArgs e) => ApplyTelaFromFields();

    private void OnResetCanvas(object sender, RoutedEventArgs e)
    {
        _liveCanvasSize = new SKPoint(_vm.Bitmap.Width, _vm.Bitmap.Height);
        UpdateTelaFields();
        Canvas.InvalidateVisual();
    }

    /// <summary>Lê os campos (em px ou %) e aplica de uma vez só.</summary>
    private void ApplyTelaFromFields()
    {
        if (!double.TryParse(TelaW.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double a) || a <= 0) return;
        if (!double.TryParse(TelaH.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double b) || b <= 0) return;

        int w = PercentMode ? (int)Math.Round(_pctBaseW * a / 100.0) : (int)Math.Round(a);
        int h = PercentMode ? (int)Math.Round(_pctBaseH * b / 100.0) : (int)Math.Round(b);
        ApplyCanvasSize(Math.Max(w, 1), Math.Max(h, 1));
    }

    private void ApplyCanvasSize(int w, int h)
    {
        _liveCanvasSize = null;
        if (w <= 0 || h <= 0 || (w == _vm.Bitmap.Width && h == _vm.Bitmap.Height)) return;
        if (_vm.ResizeImageWithCanvas)
            _vm.ApplyResize(w, h);
        else
            _vm.ApplyCanvasResize(w, h);
        UpdateTelaFields();
        Canvas.InvalidateVisual();
    }

    /// <summary>
    /// 8 alças (estilo Paint/Resolume): direita/esquerda crescem ou encolhem a tela
    /// pros dois lados, cima/baixo idem no eixo vertical, cantos combinam os dois.
    /// Como o resultado final sempre CENTRALIZA a imagem original (ver
    /// <see cref="ImageEditorViewModel.ApplyCanvasResize"/>), não importa de que lado
    /// você arrasta — o efeito prático é "abrir mais tela" e a arte fica no meio.
    /// </summary>
    private void BeginCanvasDrag(SKPoint pt)
    {
        _canvasDrag = HitCanvasHandle(pt);
        _liveCanvasSize ??= new SKPoint(_vm.Bitmap.Width, _vm.Bitmap.Height);
    }

    private CanvasHandle HitCanvasHandle(SKPoint pt)
    {
        var bmp = _vm.Bitmap;
        float tolerance = (float)(16 / Math.Max(CanvasScale(), 0.01));
        bool nearLeft = Math.Abs(pt.X) <= tolerance;
        bool nearRight = Math.Abs(pt.X - bmp.Width) <= tolerance;
        bool nearTop = Math.Abs(pt.Y) <= tolerance;
        bool nearBottom = Math.Abs(pt.Y - bmp.Height) <= tolerance;
        bool midY = pt.Y > -tolerance && pt.Y < bmp.Height + tolerance;
        bool midX = pt.X > -tolerance && pt.X < bmp.Width + tolerance;

        return (nearLeft, nearRight, nearTop, nearBottom) switch
        {
            (true, false, true, false) => CanvasHandle.NW,
            (false, true, true, false) => CanvasHandle.NE,
            (true, false, false, true) => CanvasHandle.SW,
            (false, true, false, true) => CanvasHandle.SE,
            (true, false, false, false) when midY => CanvasHandle.W,
            (false, true, false, false) when midY => CanvasHandle.E,
            (false, false, true, false) when midX => CanvasHandle.N,
            (false, false, false, true) when midX => CanvasHandle.S,
            _ => CanvasHandle.None,
        };
    }

    private void ContinueCanvasDrag(SKPoint pt)
    {
        var bmp = _vm.Bitmap;
        var live = _liveCanvasSize ?? new SKPoint(bmp.Width, bmp.Height);
        float w = live.X, h = live.Y;
        double aspect = bmp.Height > 0 ? (double)bmp.Width / bmp.Height : 1;

        // Arrastar pra fora de qualquer lado (inclusive esquerda/cima, onde a
        // coordenada fica negativa) aumenta o tamanho correspondente.
        float wFromRight() => Math.Max(pt.X, 8);
        float wFromLeft() => Math.Max(bmp.Width - pt.X, 8);
        float hFromBottom() => Math.Max(pt.Y, 8);
        float hFromTop() => Math.Max(bmp.Height - pt.Y, 8);

        switch (_canvasDrag)
        {
            case CanvasHandle.E: w = wFromRight(); if (_vm.LockAspect) h = (float)(w / aspect); break;
            case CanvasHandle.W: w = wFromLeft(); if (_vm.LockAspect) h = (float)(w / aspect); break;
            case CanvasHandle.S: h = hFromBottom(); if (_vm.LockAspect) w = (float)(h * aspect); break;
            case CanvasHandle.N: h = hFromTop(); if (_vm.LockAspect) w = (float)(h * aspect); break;
            case CanvasHandle.SE: w = wFromRight(); h = _vm.LockAspect ? (float)(w / aspect) : hFromBottom(); break;
            case CanvasHandle.SW: w = wFromLeft(); h = _vm.LockAspect ? (float)(w / aspect) : hFromBottom(); break;
            case CanvasHandle.NE: w = wFromRight(); h = _vm.LockAspect ? (float)(w / aspect) : hFromTop(); break;
            case CanvasHandle.NW: w = wFromLeft(); h = _vm.LockAspect ? (float)(w / aspect) : hFromTop(); break;
        }

        _liveCanvasSize = new SKPoint(w, h);
        _updatingTela = true;
        TelaW.Text = ((int)w).ToString(CultureInfo.InvariantCulture);
        TelaH.Text = ((int)h).ToString(CultureInfo.InvariantCulture);
        _updatingTela = false;
    }

    // ── Pintura ─────────────────────────────────────────────────────────────────

    private void OnPaintSurface(object? sender, SkiaSharp.Views.Desktop.SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(new SKColor(0x17, 0x17, 0x1A)); // mesmo InputBg do tema escuro
        var bmp = _vm.Bitmap;

        // Mesma transformação usada no hit-test (ViewTransform) — desenho e mouse
        // SEMPRE alinhados, com zoom manual da roda incluído.
        var (scale, offsetX, offsetY) = ViewTransform(e.Info.Width, e.Info.Height);
        var dest = new SKRect((float)offsetX, (float)offsetY,
            (float)(offsetX + bmp.Width * scale), (float)(offsetY + bmp.Height * scale));

        // Na ferramenta Tela, mostra a área que será criada antes de aplicar.
        if (_vm.Tool == EditorTool.Canvas && _liveCanvasSize is { } previewSize)
        {
            float extraX = (previewSize.X - bmp.Width) / 2f;
            float extraY = (previewSize.Y - bmp.Height) / 2f;
            var previewTopLeft = new SKPoint((float)(offsetX - extraX * scale), (float)(offsetY - extraY * scale));
            var previewBottomRight = new SKPoint((float)(offsetX + (bmp.Width + extraX) * scale), (float)(offsetY + (bmp.Height + extraY) * scale));
            using var previewFill = new SKPaint { Color = _vm.TransparentCanvas ? new SKColor(70, 70, 76) : SKColors.White };
            canvas.DrawRect(new SKRect(previewTopLeft.X, previewTopLeft.Y, previewBottomRight.X, previewBottomRight.Y), previewFill);
        }
        canvas.DrawBitmap(bmp, dest);

        SKPoint ToScreen(SKPoint img) => new(
            (float)(offsetX + img.X * scale), (float)(offsetY + img.Y * scale));

        // CAMADAS por cima da arte base (mesma ordem da lista: a última é a mais alta).
        // Desenhadas no espaço de tela — por isso o canvas é transformado pra escala
        // da visualização enquanto elas são compostas.
        if (_vm.Layers.Count > 0)
        {
            canvas.Save();
            canvas.Translate((float)offsetX, (float)offsetY);
            canvas.Scale((float)scale);
            foreach (var layer in _vm.Layers) layer.Draw(canvas);
            canvas.Restore();
        }

        // Moldura + alças da camada selecionada (só na ferramenta Camadas).
        if (_vm.Tool == EditorTool.Layers && _vm.ActiveLayer is { } active)
        {
            var b = active.Bounds;
            var p1 = ToScreen(new SKPoint(b.Left, b.Top));
            var p2 = ToScreen(new SKPoint(b.Right, b.Bottom));
            var box = new SKRect(p1.X, p1.Y, p2.X, p2.Y);
            using (var pen = new SKPaint { Color = new SKColor(0x9A, 0xCD, 0x32), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f })
                canvas.DrawRect(box, pen);
            using var grip = new SKPaint { Color = new SKColor(0x9A, 0xCD, 0x32), Style = SKPaintStyle.Fill };
            const float hs = 5;
            foreach (var c in new[]
            {
                new SKPoint(box.Left, box.Top), new SKPoint(box.Right, box.Top),
                new SKPoint(box.Left, box.Bottom), new SKPoint(box.Right, box.Bottom),
            })
                canvas.DrawRect(new SKRect(c.X - hs, c.Y - hs, c.X + hs, c.Y + hs), grip);
        }

        if (_vm.Tool == EditorTool.Crop && _liveCropRect is { } crop)
        {
            var a = ToScreen(new SKPoint(Math.Min(crop.Left, crop.Right), Math.Min(crop.Top, crop.Bottom)));
            var b = ToScreen(new SKPoint(Math.Max(crop.Left, crop.Right), Math.Max(crop.Top, crop.Bottom)));
            var sel = new SKRect(a.X, a.Y, b.X, b.Y);

            // VÉU sobre o que será DESCARTADO (estilo Paint 3D): o resto da imagem
            // continua visível, só escurecido. É isso que dá a leitura imediata do
            // enquadramento — só o contorno verde não mostrava o que se perde.
            using (var veil = new SKPaint { Color = new SKColor(0, 0, 0, 140) })
            {
                canvas.Save();
                canvas.ClipRect(sel, SKClipOperation.Difference);  // tudo MENOS a seleção
                canvas.DrawRect(dest, veil);
                canvas.Restore();
            }

            // Moldura clara + guias de terços (ajudam a enquadrar, como no Paint 3D).
            using (var border = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f })
                canvas.DrawRect(sel, border);
            using (var thirds = new SKPaint { Color = new SKColor(255, 255, 255, 90), Style = SKPaintStyle.Stroke, StrokeWidth = 1 })
            {
                for (int i = 1; i <= 2; i++)
                {
                    float x = sel.Left + sel.Width * i / 3f;
                    float y = sel.Top + sel.Height * i / 3f;
                    canvas.DrawLine(x, sel.Top, x, sel.Bottom, thirds);
                    canvas.DrawLine(sel.Left, y, sel.Right, y, thirds);
                }
            }

            // 8 alças (4 cantos + 4 arestas), como no Paint 3D — brancas com contorno
            // escuro pra ficarem visíveis tanto sobre foto clara quanto escura.
            using var grip = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill };
            using var gripEdge = new SKPaint { Color = new SKColor(0, 0, 0, 160), Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
            const float hs = 5;
            float midX = (sel.Left + sel.Right) / 2, midY = (sel.Top + sel.Bottom) / 2;
            foreach (var c in new[]
            {
                new SKPoint(sel.Left, sel.Top), new SKPoint(sel.Right, sel.Top),
                new SKPoint(sel.Left, sel.Bottom), new SKPoint(sel.Right, sel.Bottom),
                new SKPoint(midX, sel.Top), new SKPoint(midX, sel.Bottom),
                new SKPoint(sel.Left, midY), new SKPoint(sel.Right, midY),
            })
            {
                var r = new SKRect(c.X - hs, c.Y - hs, c.X + hs, c.Y + hs);
                canvas.DrawRect(r, grip);
                canvas.DrawRect(r, gripEdge);
            }

            using var labelBg = new SKPaint { Color = new SKColor(20, 20, 24, 220) };
            using var labelText = new SKPaint { Color = SKColors.White, TextSize = 12, IsAntialias = true };
            string dimensions = $"{Math.Max((int)Math.Round(Math.Abs(crop.Width)), 1)} × {Math.Max((int)Math.Round(Math.Abs(crop.Height)), 1)} px";
            float labelWidth = labelText.MeasureText(dimensions) + 14;
            float labelY = sel.Top > 28 ? sel.Top - 26 : sel.Top + 8;
            canvas.DrawRoundRect(new SKRect(sel.Left, labelY, sel.Left + labelWidth, labelY + 20), 3, 3, labelBg);
            canvas.DrawText(dimensions, sel.Left + 7, labelY + 14, labelText);
        }

        // Guias da TELA (estilo Paint): contorno provisório CENTRADO na imagem atual —
        // o resultado final sempre centraliza (ApplyCanvasResize), então a prévia já
        // mostra pra onde a arte vai, não importa de que lado você arrastou a alça.
        if (_vm.Tool == EditorTool.Canvas)
        {
            var size = _liveCanvasSize ?? new SKPoint(bmp.Width, bmp.Height);
            float offX = (size.X - bmp.Width) / 2f;
            float offY = (size.Y - bmp.Height) / 2f;
            var o = ToScreen(new SKPoint(-offX, -offY));
            var c2 = ToScreen(new SKPoint(bmp.Width + offX, bmp.Height + offY));
            using var outline = new SKPaint
            {
                Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 1,
                PathEffect = SKPathEffect.CreateDash([4f, 4f], 0),
            };
            canvas.DrawRect(new SKRect(o.X, o.Y, c2.X, c2.Y), outline);

            using var handle = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill };
            const float hs = 5;
            float midX = (o.X + c2.X) / 2, midY = (o.Y + c2.Y) / 2;
            foreach (var p in new[]
            {
                new SKPoint(c2.X, midY), new SKPoint(o.X, midY),   // direita, esquerda
                new SKPoint(midX, c2.Y), new SKPoint(midX, o.Y),   // inferior, cima
                new SKPoint(c2.X, c2.Y), new SKPoint(o.X, c2.Y),   // cantos inferiores
                new SKPoint(c2.X, o.Y), new SKPoint(o.X, o.Y),     // cantos superiores
            })
                canvas.DrawRect(new SKRect(p.X - hs, p.Y - hs, p.X + hs, p.Y + hs), handle);
        }

        if (_liveShape is { } shape)
        {
            using var paint = new SKPaint
            {
                Color = _vm.BrushColor.WithAlpha(180),
                Style = _vm.ShapeFilled && _vm.Shape != ShapeKind.Linha ? SKPaintStyle.Fill : SKPaintStyle.Stroke,
                StrokeWidth = (float)(_vm.BrushSize * scale),
                IsAntialias = true,
            };
            var a = ToScreen(shape.A);
            var b = ToScreen(shape.B);
            var rect = new SKRect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
            switch (_vm.Shape)
            {
                case ShapeKind.Retângulo: canvas.DrawRect(rect, paint); break;
                case ShapeKind.Elipse: canvas.DrawOval(rect, paint); break;
                case ShapeKind.Linha: canvas.DrawLine(a, b, paint); break;
            }

            // Contorno + alças: a forma fica pendente (editável) até "Concluir forma".
            using var outlinePaint = new SKPaint { Color = SKColors.Lime, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
            canvas.DrawRect(rect, outlinePaint);
            using var handlePaint = new SKPaint { Color = SKColors.Lime, Style = SKPaintStyle.Fill };
            const float hs = 5;
            foreach (var c in new[] { new SKPoint(rect.Left, rect.Top), new SKPoint(rect.Right, rect.Top),
                                       new SKPoint(rect.Left, rect.Bottom), new SKPoint(rect.Right, rect.Bottom) })
                canvas.DrawRect(new SKRect(c.X - hs, c.Y - hs, c.X + hs, c.Y + hs), handlePaint);
        }
    }
}
