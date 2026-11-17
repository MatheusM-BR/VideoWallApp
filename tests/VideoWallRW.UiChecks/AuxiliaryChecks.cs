using System.Dynamic;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using VideoWallRW.App;
using VideoWallRW.App.ViewModels;
using VideoWallRW.App.Controls;
using SkiaSharp;

internal static class AuxiliaryChecks
{
    static dynamic Obj(params (string, object?)[] items)
    {
        IDictionary<string, object?> value = new ExpandoObject();
        foreach (var (key, item) in items) value[key] = item;
        return (ExpandoObject)value;
    }
    static void Frame() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    static IEnumerable<DependencyObject> Children(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var item in Children(child)) yield return item;
        }
    }
    static void Save(Window win, string name)
    {
        Frame(); win.UpdateLayout();
        var root = (FrameworkElement)win.Content;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create($"artifacts/ui-review/layout-{name}.png"); encoder.Save(stream);
        foreach (var grid in Children(root).OfType<Grid>().Where(g => g.Name.EndsWith("Workspace")))
            if (grid.ColumnDefinitions.Sum(c => c.ActualWidth) > grid.ActualWidth + 1) throw new Exception($"Overflow in {grid.Name}");
        Console.WriteLine($"Rendered {name}: {root.ActualWidth:F0}x{root.ActualHeight:F0}");
    }
    static void Show(Window win)
    {
        win.ShowActivated = false; win.ShowInTaskbar = false; win.Left = -20000; win.Top = -20000; win.Show(); Frame(); WindowSizing.KeepDesignSize(win);
    }
    static Window FixtureWindow(string file, object data)
    {
        var xaml = NeutralDemo.Xaml(File.ReadAllText($"src/VideoWallRW.App/{file}Window.xaml"));
        xaml = Regex.Replace(xaml, " x:Class=\"[^\"]*\"", "");
        xaml = Regex.Replace(xaml, "\\s+\\w+=\"On[^\"]*\"", "");
        xaml = xaml.Replace("Source=\"Themes/Auxiliary.xaml\"", "Source=\"/VideoWallRW.App;component/Themes/Auxiliary.xaml\"");
        xaml = xaml.Replace("clr-namespace:VideoWallRW.App.Controls", "clr-namespace:VideoWallRW.App.Controls;assembly=VideoWallRW.App");
        var win = (Window)XamlReader.Parse(xaml); win.DataContext = data; return win;
    }
    public static void Run(Application app)
    {
        var corner = ImageSelectionGeometry.FromCorner(new SKPoint(100, 100), new SKPoint(900, 900), 640, 360, 16d / 9);
        if (corner.Left < 0 || corner.Top < 0 || corner.Right > 640 || corner.Bottom > 360 || Math.Abs(corner.Width / corner.Height - 16d / 9) > .01)
            throw new Exception("Crop corner geometry escaped the image or lost aspect ratio");
        var byHeight = ImageSelectionGeometry.FitSize(new SKRect(500, 300, 600, 350), 100, 180, 640, 360, 1, heightEdited: true);
        if (byHeight.Right > 640 || byHeight.Bottom > 360 || Math.Abs(byHeight.Width - byHeight.Height) > .01)
            throw new Exception("Crop numeric sizing failed");
        foreach (var edge in new[] { "Left", "Right", "Top", "Bottom" })
        {
            var resized = ImageSelectionGeometry.ResizeEdge(new SKRect(100, 80, 500, 280), edge, new SKPoint(-100, 500), 640, 360, 16d / 9);
            if (resized.Left < -.01 || resized.Top < -.01 || resized.Right > 640.01 || resized.Bottom > 360.01)
                throw new Exception("Crop edge geometry escaped image: " + edge);
        }
        Console.WriteLine("Crop geometry checks passed for corners, edges, aspect ratio and numeric height edits.");
        using (var behaviorVm = new ImageEditorViewModel(Path.GetFullPath("artifacts/ui-review/demo-clip-0.png")))
        {
            behaviorVm.LayerMode = true;
            behaviorVm.AddImageLayer(Path.GetFullPath("artifacts/ui-review/demo-clip-1.png"));
            var floating = behaviorVm.ActiveLayer ?? throw new Exception("Layer selection failed");
            float originalX = floating.X, originalY = floating.Y;
            behaviorVm.ApplyCrop(new SKRect(40, 30, 600, 330));
            if (behaviorVm.Bitmap.Width != 560 || behaviorVm.Bitmap.Height != 300 ||
                Math.Abs(behaviorVm.ActiveLayer!.X - (originalX - 40)) > .01 || Math.Abs(behaviorVm.ActiveLayer.Y - (originalY - 30)) > .01)
                throw new Exception("Crop did not preserve layer alignment");
            behaviorVm.UndoCommand.Execute(null);
            if (behaviorVm.Bitmap.Width != 640 || behaviorVm.Bitmap.Height != 360 || behaviorVm.Layers.Count != 1) // camada adicionada
                throw new Exception("Undo did not restore the complete composition");
            behaviorVm.ApplyCanvasResize(800, 500);
            if (behaviorVm.Bitmap.Width != 800 || behaviorVm.ActiveLayer!.X <= originalX)
                throw new Exception("Canvas expansion did not move floating material with the base");
            behaviorVm.UndoCommand.Execute(null);
            if (behaviorVm.Bitmap.Width != 640 || Math.Abs(behaviorVm.ActiveLayer!.X - originalX) > .01)
                throw new Exception("Undo canvas expansion lost material position");
        }
        Console.WriteLine("Crop, layer alignment, canvas expansion and complete undo checks passed.");
        using (var layersVm = new ImageEditorViewModel(Path.GetFullPath("artifacts/ui-review/demo-clip-0.png")))
        {
            // Padrão: camadas mescladas — a foto abre como a própria imagem.
            if (layersVm.Layers.Count != 0 || layersVm.LayerMode)
                throw new Exception("Editor did not open with merged layers by default");
            layersVm.Tool = EditorTool.Shape;
            layersVm.AddShapeLayer(new SKPoint(10, 10), new SKPoint(110, 60));
            layersVm.Tool = EditorTool.Brush;
            if (layersVm.Layers.Count != 0)
                throw new Exception("Merged mode did not fuse the shape when switching tools");
            layersVm.UndoCommand.Execute(null);
            layersVm.LayerMode = true;
            layersVm.Tool = EditorTool.Shape;
            if (layersVm.AddShapeLayer(new SKPoint(10, 10), new SKPoint(110, 60)) is not { Kind: EditorLayerKind.Shape } shape ||
                Math.Abs(shape.Width - 100) > .01 || !ReferenceEquals(layersVm.ActiveLayer, shape))
                throw new Exception("Shape was not inserted as a selected layer on release");
            layersVm.Tool = EditorTool.Text;
            var text = layersVm.AddTextLayer(new SKRect(20, 20, 300, 120)) ?? throw new Exception("Text layer not created");
            layersVm.TextInput = "Evento 1";
            if (text.Text != "Evento 1" || layersVm.Layers.Count != 2)
                throw new Exception("Editing the text field did not update the selected text layer");
            layersVm.UndoCommand.Execute(null);
            if (layersVm.Layers.OfType<EditorLayer>().Any(l => l.Kind == EditorLayerKind.Text && l.Text == "Evento 1"))
                throw new Exception("Text edit was not undoable");
            if (ImageEditorViewModel.EditedFileName(1920, 1080) != "16x9 - Edited.png" ||
                ImageEditorViewModel.EditedFileName(1080, 1920) != "9x16 - Edited.png" ||
                ImageEditorViewModel.EditedFileName(1920, 1080, 3) != "16x9 - Edited v3.png")
                throw new Exception("Edited file name does not follow the 16x9/9x16 pattern");
        }
        Console.WriteLine("Merged-by-default, layer mode, shape/text layers and edited file name checks passed.");
        var path = Path.GetFullPath("artifacts/ui-review/demo-clip-0.png");
        var editorVm = new ImageEditorViewModel(path);
        var editor = new ImageEditorWindow(editorVm);
        Show(editor); Save(editor, "editor-paint3d");
        WindowSizing.Resize(editor, 1000, 680);
        // Camadas de exemplo pros prints do manual: uma faixa (forma) e um texto por cima da arte.
        editorVm.LayerMode = true;
        editorVm.Tool = EditorTool.Shape; editorVm.SetColorHex("#ED1C24");
        var demoShape = editorVm.AddShapeLayer(new SKPoint(24, 24), new SKPoint(250, 84))!;
        editorVm.Tool = EditorTool.Text; editorVm.SetColorHex("#FFFFFF");
        var demoText = editorVm.AddTextLayer(new SKRect(36, 30, 240, 78))!;
        editorVm.TextInput = "EVENTO 1";
        foreach (var tool in Enum.GetValues<EditorTool>())
        {
            // Categorias pelo botão real, subferramentas pelos radios reais.
            var category = Children(editor).OfType<Button>().FirstOrDefault(b => b.Tag is EditorTool t && t == tool);
            if (category is not null) category.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            else
            {
                var brushCategory = Children(editor).OfType<Button>().First(b => b.Tag is EditorTool.Brush);
                brushCategory.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Frame();
                var radio = Children(editor).OfType<RadioButton>().First(b => System.Windows.Data.BindingOperations.GetBinding(b, ToggleButton.IsCheckedProperty)?.ConverterParameter is EditorTool t && t == tool);
                radio.IsChecked = true;
            }
            editorVm.ActiveLayer = tool switch
            {
                EditorTool.Shape => demoShape,
                EditorTool.Text => demoText,
                _ => editorVm.Layers[0], // imagem original
            };
            Frame();
            if (editorVm.Tool != tool) throw new Exception("Tool selection failed: " + tool);
            Save(editor, "editor-" + tool.ToString().ToLowerInvariant());
        }
        // Seleção: marca uma área do fundo e estica ela pra direita (pedaço flutuando,
        // com alças e o rótulo de tamanho/%) — print do manual e conferência visual.
        Children(editor).OfType<Button>().First(b => b.Tag is EditorTool.Select).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Frame();
        int bw = editorVm.Bitmap.Width, bh = editorVm.Bitmap.Height;
        var area = new SKRect(bw * 0.45f, bh * 0.40f, bw * 0.65f, bh * 0.75f);
        editorVm.SetSelection(area);
        if (!editorVm.LiftSelection()) throw new Exception("Selection lift failed");
        editorVm.SetFloatingRect(new SKRect(area.Left, area.Top, bw * 0.95f, area.Bottom));
        Frame();
        if (editorVm.Selection is not { } stretched || Math.Abs(stretched.Right - bw * 0.95f) > 0.5f)
            throw new Exception("Selection stretch failed");
        if (((TextBox)editor.FindName("SelectW")).Text != ((int)Math.Round(stretched.Width)).ToString())
            throw new Exception("Selection width field did not follow the stretch");
        Save(editor, "editor-select-stretch");
        // Giro + espelho: contorno e alças girados, campo de giro preenchido.
        editorVm.SetSelectionAngle(-15);
        editorVm.FlipSelection(horizontal: false);
        Frame();
        if (((TextBox)editor.FindName("SelectAngle")).Text != "15")
            throw new Exception("Selection angle field did not follow the rotation/flip");
        Save(editor, "editor-select-rotate");
        editorVm.CommitSelection();
        Frame();
        Save(editor, "editor-select-applied");
        // Varinha mágica na faixa vermelha (a área marcada segue o formato, sem as letras).
        editorVm.SelectMode = SelectShape.Varinha;
        editorVm.SelectFeather = 4;
        editorVm.SelectByColorAt(new SKPoint(28, 28));
        Frame();
        if (!editorVm.HasShapedSelection || editorVm.Selection is not { } wand || wand.Width < 150)
            throw new Exception("Magic wand did not select the red band");
        Save(editor, "editor-select-wand");
        editorVm.SetSelection(null);
        editorVm.SelectMode = SelectShape.Retângulo;
        editorVm.SelectFeather = 0;
        // Camada girada e espelhada: moldura e bolinha de giro acompanham.
        editorVm.Tool = EditorTool.Layers;
        editorVm.ActiveLayer = demoShape;
        editorVm.RotateLayerCommand.Execute("horario");
        demoShape.Rotation = -8;
        Frame();
        Save(editor, "editor-layer-rotate");
        editorVm.ResetLayerRotationCommand.Execute(null);
        editorVm.Tool = EditorTool.Crop;
        Frame();
        Children(editor).OfType<Button>().First(b => Equals(b.Content, "+")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        if (((TextBlock)editor.FindName("ZoomLabel")).Text.StartsWith("100")) throw new Exception("Zoom button failed");
        Children(editor).OfType<Button>().First(b => Equals(b.Content, "Ajustar à tela")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        if (!((TextBlock)editor.FindName("ZoomLabel")).Text.StartsWith("100")) throw new Exception("Fit button failed");
        editor.Close();

        var events = new List<object> { Obj(("Title", "Evento 1 — apresentação de abertura"), ("Subtitle", "14:00 · Estúdio 1")), Obj(("Title", "Evento 2 — cenário especial"), ("Subtitle", "17:00 · Estúdio 2")), Obj(("Title", "Evento 3 — encerramento"), ("Subtitle", "20:00 · Estúdio 1")) };
        dynamic details = Obj(("Title", "Evento 1 — apresentação de abertura"), ("BannerUrl", path), ("BannerPreviewUrl", path), ("Downloads", new[] { Obj(("Label", "Catálogo do evento · PDF")), Obj(("Label", "Regulamento · PDF")) }));
        dynamic agendaData = Obj(("BannerPreviewPath", path), ("Date", new DateOnly(2026,9,8)), ("Events", events), ("SelectedEvent", events[0]), ("Details", details), ("Loading", false), ("LoadingDetails", false), ("Downloading", false), ("DownloadProgress", .4), ("StatusText", "DEMONSTRAÇÃO · Dados fictícios; nenhum download realizado."));
        foreach (var command in new[] { "PreviousDayCommand", "NextDayCommand", "TodayCommand", "SaveBannerCommand", "SaveBannerAndEditCommand", "DownloadCatalogCommand", "DownloadRegulationCommand" }) ((IDictionary<string, object?>)agendaData)[command] = null;
        Window agenda = FixtureWindow("Agenda", (object)agendaData); Show(agenda); Save(agenda, "agenda");
        WindowSizing.Resize(agenda, 940, 640);
        Children(agenda).OfType<Expander>().Single().IsExpanded = true; Save(agenda, "agenda-documents");
        agendaData.Details = null; agendaData.LoadingDetails = true; Save(agenda, "agenda-loading");
        agendaData.LoadingDetails = false; agendaData.SelectedEvent = null; agendaData.Events = new List<object>(); Save(agenda, "agenda-empty");
        agendaData.Details = details; agendaData.Events = events; agendaData.SelectedEvent = events[0]; agendaData.Downloading = true; Save(agenda, "agenda-download");
        agenda.Close();
        var pages = new[] { Obj(("Label", "Página 1"), ("Image", path)), Obj(("Label", "Página 2"), ("Image", path)) };
        dynamic pdfData = Obj(("Pages", pages), ("SelectedPage", pages[0]), ("PageCount", 2), ("Dpi", 300), ("StatusText", "DEMONSTRAÇÃO · Página fictícia."), ("ExportPageCommand", null));
        Window pdf = FixtureWindow("PdfImport", (object)pdfData);
        // A prévia do PDF usa PaintSurface; neste ensaio só de layout é substituída por uma imagem fictícia.
        var preview = (SkiaSharp.Views.WPF.SKElement)pdf.FindName("PreviewElement");
        ((Border)preview.Parent).Child = new Image { Source = new BitmapImage(new Uri(path)), Stretch = Stretch.Uniform, Margin = new Thickness(32) };
        Show(pdf); Save(pdf, "pdf"); WindowSizing.Resize(pdf, 860, 620); Save(pdf, "pdf-small"); pdf.Close();
        CompletionChecks.CheckAuxiliaryWindows();
        app.Shutdown(); Console.WriteLine("Auxiliary visual checks passed.");
    }
}
