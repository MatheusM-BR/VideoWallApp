using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Dynamic;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;

internal static class Program
{
    static dynamic Obj(params (string, object?)[] items)
    {
        IDictionary<string, object?> value = new ExpandoObject();
        foreach (var (key, item) in items) value[key] = item;
        return (ExpandoObject)value;
    }
    static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var item in Descendants(child)) yield return item;
        }
    }
    static void Frame() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    static void Snapshot(FrameworkElement root, string name)
    {
        Frame(); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.GetFullPath($"artifacts/ui-review/layout-{name}.png"));
        encoder.Save(output);
        Console.WriteLine($"Rendered {name}: {root.ActualWidth} × {root.ActualHeight}");
    }
    [STAThread]
    static void Main(string[] args)
    {
        Directory.CreateDirectory("artifacts/ui-review");
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var theme = File.ReadAllText("src/VideoWallRW.App/Themes/Dark.xaml").Replace("pack://application:,,,/Assets/AppIcon.ico", "pack://application:,,,/VideoWallRW.App;component/Assets/AppIcon.ico");
        app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(theme));
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/VideoWallRW.App;component/Themes/Auxiliary.xaml", UriKind.Relative) });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/VideoWallRW.App;component/Assets/Brand.xaml", UriKind.Relative) });
        var assembly = typeof(VideoWallRW.App.MainWindow).Assembly;
        foreach (var name in new[] { "IdentifyLabelConverter", "OperationLabelConverter", "NullToVisibilityConverter", "NotNullToVisibilityConverter", "EnumBoolConverter", "IncrementConverter", "InverseBoolConverter", "InverseBoolToVisibilityConverter", "FileNameConverter", "ResizeModeLabelConverter", "ClipBaseDimensionConverter", "ZeroToVisibilityConverter" })
            app.Resources[name] = Activator.CreateInstance(assembly.GetType($"VideoWallRW.App.{name}")!);
        app.Resources["BoolToVisibilityConverter"] = new BooleanToVisibilityConverter();
        
        app.Resources["OperationLabelTemplate"] = XamlReader.Parse("<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><TextBlock Text=\"{Binding Converter={StaticResource OperationLabelConverter}}\" /></DataTemplate>");
        if (args.Contains("--auxiliary")) { AuxiliaryChecks.Run(app); return; }
        // O mesmo XAML e controles do aplicativo, com dados fictícios. Nenhum engine, saída ou projeto é iniciado.
        var xaml = NeutralDemo.Xaml(File.ReadAllText("src/VideoWallRW.App/MainWindow.xaml"));
        xaml = Regex.Replace(xaml, " x:Class=\"[^\"]*\"", "");
        xaml = Regex.Replace(xaml, "\\s+(?:PreviewKeyDown|Click|Drop|PreviewMouseLeftButtonDown|MouseDoubleClick|PreviewMouseMove)=\"On[^\"]*\"", "");
        xaml = xaml.Replace("clr-namespace:VideoWallRW.App.Controls", "clr-namespace:VideoWallRW.App.Controls;assembly=VideoWallRW.App")
            .Replace("Source=\"Themes/Workspace.xaml\"", "Source=\"/VideoWallRW.App;component/Themes/Workspace.xaml\"")
            .Replace("Source=\"Assets/AppIcon.png\"", "Source=\"pack://application:,,,/VideoWallRW.App;component/Assets/AppIcon.png\"");
        var win = (Window)XamlReader.Parse(xaml);
        // Todos os comandos são inertes no ensaio visual.
        var doc = XDocument.Parse(xaml);
        var commandNames = doc.Descendants().Attributes().Where(a => a.Name.LocalName == "Command")
            .Select(a => Regex.Match(a.Value, @"Binding ([\w.]+)").Groups[1].Value.Split('.').Last()).Distinct();
        dynamic fixture = Obj(); dynamic grid = Obj();
        foreach (var command in commandNames) { ((IDictionary<string, object?>)fixture)[command] = null; ((IDictionary<string, object?>)grid)[command] = null; }
        var assets = new[] { "EVENTO 1", "EVENTO 2", "CENÁRIO A", "CENÁRIO B", "ESTÚDIO 1", "FUNDO" };
        var pictures = new string[assets.Length];
        for (var i = 0; i < assets.Length; i++)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var colors = new[] { "#143A6A", "#62306D", "#18595B", "#6C442B", "#344E60", "#294534" };
                dc.DrawRectangle(new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i])), null, new Rect(0, 0, 640, 360));
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(36, 255, 255, 255)), null, new Point(520, 80), 230, 230);
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)), null, new Rect(0, 250, 640, 2));
                dc.DrawText(new FormattedText(assets[i], System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 42, Brushes.White, 1), new Point(34, 145));
                dc.DrawText(new FormattedText("CONTEÚDO DE DEMONSTRAÇÃO", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 16, Brushes.White, 1), new Point(36, 270));
            }
            var bmp = new RenderTargetBitmap(640, 360, 96, 96, PixelFormats.Pbgra32); bmp.Render(visual);
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
            pictures[i] = Path.GetFullPath($"artifacts/ui-review/demo-clip-{i}.png");
            using var stream = File.Create(pictures[i]); enc.Save(stream);
        }
        var layers = new List<object>(); var allClips = new List<dynamic>();
        for (int row = 0; row < 3; row++)
        {
            var clips = new List<object>();
            for (int col = 0; col < 8; col++)
            {
                dynamic c = Obj(("Name", col < 6 ? assets[col] + (row == 1 ? " · B" : "") : "Vazio"), ("HasMedia", col < 6),
                    ("IsActive", col == 0), ("IsSelected", row == 0 && col == 1), ("IsMultiSelected", false), ("LinkGroupId", null),
                    ("ThumbnailPath", col < 6 ? pictures[col] : null), ("PreviewImagePath", col < 6 ? pictures[col] : null),
                    ("PlayMode", VideoWallRW.Core.Model.PlayMode.Loop), ("Autopilot", VideoWallRW.Core.Model.AutopilotAction.None),
                    ("InPointText", "0"), ("OutPointText", "30"), ("ImageDurationText", "30"), ("Speed", 1.0), ("Duration", 30.0), ("TrimIn", 0.0), ("TrimOut", 30.0),
                    ("ScrubSeconds", -1.0), ("PlayheadSeconds", 12.0), ("Resize", VideoWallRW.Core.Model.ResizeMode.Fit), ("EffectiveWidth", 1920), ("EffectiveHeight", 1080),
                    ("Volume", 0.75), ("Mute", false), ("ScaleX", 1.0), ("ScaleY", 1.0), ("PositionX", 0.0), ("PositionY", 0.0), ("SourceWidth", 1920.0), ("SourceHeight", 1080.0));
                foreach (var command in commandNames) ((IDictionary<string, object?>)c)[command] = null;
                clips.Add(c); allClips.Add(c);
            }
            dynamic layer = Obj(("Name", new[] { "01 · Principal", "02 · Conteúdo", "03 · Identidade" }[row]), ("Clips", clips), ("IgnoreColumnTrigger", row == 2),
                ("AutopilotEnabled", true), ("LayerAutopilot", VideoWallRW.Core.Model.AutopilotAction.None), ("Bypass", false), ("Solo", false), ("Blend", VideoWallRW.Core.Model.BlendMode.Normal), ("Opacity", 1.0), ("TransitionSeconds", 0.5));
            foreach (var command in commandNames) ((IDictionary<string, object?>)layer)[command] = null;
            layers.Add(layer);
        }
        grid.Layers = layers; grid.Columns = Enumerable.Range(0, 8).ToList(); grid.SelectedClip = allClips[1]; grid.Model = Obj(("Width", 3840), ("Height", 1080));
        grid.CanUndoGrid = false; grid.RecentSources = new[] { "Evento 1.mp4", "Cenário A.png" };
        grid.PlayModes = Enum.GetValues<VideoWallRW.Core.Model.PlayMode>(); grid.AutopilotActions = Enum.GetValues<VideoWallRW.Core.Model.AutopilotAction>();
        grid.BlendModes = Enum.GetValues<VideoWallRW.Core.Model.BlendMode>(); grid.ResizeModes = Enum.GetValues<VideoWallRW.Core.Model.ResizeMode>();
        fixture.Grid = grid; fixture.WindowTitle = "RWVideoWall — Demonstração do layout"; fixture.Blackout = false; fixture.MasterFader = 1.0; fixture.AutopilotAll = true;
        fixture.SequencerRunning = false; fixture.RemoteRunning = false; fixture.OscEnabled = false; fixture.HardwareDecode = false; fixture.RemoteButtonText = "Controle remoto"; fixture.CompositionMonitorEnabled = false; fixture.CompositionMonitorBitmap = null; fixture.ActiveClipThumbnailPath = pictures[0]; fixture.ShowMonitorPlaceholderText = false;
        fixture.ShowSourcesTab = false; fixture.NdiAvailable = false; fixture.NdiUnavailableReason = "Nenhuma fonte conectada nesta demonstração."; fixture.NdiSources = Array.Empty<string>();
        fixture.StatusText = "DEMONSTRAÇÃO DO LAYOUT  ·  Dados fictícios; nenhuma saída conectada";
        fixture.Scenes = new[] { Obj(("Name", "Evento 1"), ("IsSelected", true), ("TriggerCommand", null), ("RenameCommand", null), ("RemoveCommand", null)), Obj(("Name", "Cenário 2"), ("IsSelected", false), ("TriggerCommand", null), ("RenameCommand", null), ("RemoveCommand", null)), Obj(("Name", "Estúdio"), ("IsSelected", false), ("TriggerCommand", null), ("RenameCommand", null), ("RemoveCommand", null)) };
        fixture.Monitors = new[] { Obj(("Title", "Tela 1 · 1920 × 1080"), ("IsActive", false), ("RouteOptions", new[] { "Composição", "01 · Principal" }), ("SelectedRouteIndex", 0), ("FpsText", "Desligada")), Obj(("Title", "Tela 2 · 1920 × 1080"), ("IsActive", false), ("RouteOptions", new[] { "Composição", "01 · Principal" }), ("SelectedRouteIndex", 0), ("FpsText", "Desligada")) };
        var files = assets.Select((name, i) => (object)Obj(("Name", name + ".mp4"), ("Icon", "▸"), ("ThumbnailPath", pictures[i]), ("IsFolder", false))).ToList();
        dynamic browser = Obj(("CurrentFolder", @"C:\Video Wall\Evento 1"), ("Items", files), ("SelectedItem", files[1]));
        foreach (var command in commandNames) ((IDictionary<string, object?>)browser)[command] = null;
        fixture.FileBrowser = browser;
        win.DataContext = fixture; win.ShowInTaskbar = false; win.ShowActivated = false; win.Left = -20000; win.Top = -20000;
        win.Show(); Frame();
        var root = (FrameworkElement)win.Content;
        foreach (var (width, height, label) in new[] { (1440, 900, "1440"), (1100, 720, "1100"), (1920, 1080, "1920") })
        {
            win.Width = width; win.Height = height; Frame(); Snapshot(root, label);
            var inspector = (FrameworkElement)win.FindName("ClipInspector");
            var inspectorScroll = Descendants(inspector).OfType<ScrollViewer>().First();
            if (width == 1440)
            {
                if (inspectorScroll.ScrollableWidth > 1 || inspectorScroll.ScrollableHeight > 1)
                    throw new Exception("Default inspector should fit without scrolling");
                Snapshot(inspector, "size-controls");
            }
            if (width == 1100)
            {
                inspectorScroll.ScrollToRightEnd(); inspectorScroll.ScrollToBottom(); Frame();
                if (inspectorScroll.HorizontalOffset < 100) throw new Exception("Narrow inspector must remain scrollable");
                Snapshot(inspector, "size-controls-small");
                inspectorScroll.ScrollToHome(); inspectorScroll.ScrollToLeftEnd(); Frame();
            }
            var work = (Grid)win.FindName("WorkspaceGrid");
            Console.WriteLine($"  workspace={work.ActualWidth:F0}x{work.ActualHeight:F0}; columns={string.Join(",", work.ColumnDefinitions.Select(c => c.ActualWidth.ToString("F0")))}; rows={string.Join(",", work.RowDefinitions.Select(r => r.ActualHeight.ToString("F0")))}");
            if (work.ColumnDefinitions.Sum(c => c.ActualWidth) > work.ActualWidth + 1) throw new Exception("Workspace width overflow");
            if (work.RowDefinitions.Sum(r => r.ActualHeight) > work.ActualHeight + 1) throw new Exception("Workspace height overflow");
        }
        // Print do manual: aba Fontes / NDI com fontes de exemplo, em 1440×900.
        win.Width = 1440; win.Height = 900;
        fixture.NdiAvailable = true; fixture.NdiSources = new[] { "ESTUDIO-1 (Câmera PTZ)", "ESTUDIO-2 (Câmera Plano Geral)", "VMIX-PC (Output 1)" };
        fixture.ShowSourcesTab = true; Frame(); Snapshot(root, "sources");
        fixture.ShowSourcesTab = false; fixture.NdiAvailable = false; fixture.NdiSources = Array.Empty<string>(); Frame();
        CheckDimensions();
        CompletionChecks.CheckInteractions(win);
        CompletionChecks.CheckDpi(root);
        win.Close(); app.Shutdown();
    }
    static void CheckDimensions()
    {
        var converter = new VideoWallRW.App.ClipBaseDimensionConverter();
        var culture = System.Globalization.CultureInfo.GetCultureInfo("pt-BR");
        double Basis(VideoWallRW.Core.Model.ResizeMode mode, string axis) => (double)converter.Convert(
            new object[] { 1005, 565, 1920, 1080, mode }, typeof(double), axis, culture);
        void Near(double actual, double expected, string label)
        {
            if (Math.Abs(actual - expected) > .01) throw new Exception($"{label}: {actual} != {expected}");
        }
        Near(Basis(VideoWallRW.Core.Model.ResizeMode.Original, "Width"), 1005, "Original width");
        Near(Basis(VideoWallRW.Core.Model.ResizeMode.Fit, "Width"), 1920, "Fit width");
        Near(Basis(VideoWallRW.Core.Model.ResizeMode.Fit, "Height"), 1079.402985, "Fit height");
        Near(Basis(VideoWallRW.Core.Model.ResizeMode.Stretch, "Height"), 1080, "Stretch height");
        Near(Basis(VideoWallRW.Core.Model.ResizeMode.Fill, "Height"), 1080, "Fill height");
        var clip = new VideoWallRW.App.ViewModels.ClipViewModel(new VideoWallRW.Core.Model.Clip(new VideoWallRW.Core.Model.FileImageSource("demo.png")), 0, 0, null!, _ => {}, _ => {});
        var slider = new VideoWallRW.App.Controls.ValueSlider
            { Minimum = .05, Maximum = 8, DefaultValue = 1, DisplayMultiplier = 1920, Format = "F0", Unit = " px" };
        System.Windows.Data.BindingOperations.SetBinding(slider, VideoWallRW.App.Controls.ValueSlider.ValueProperty,
            new System.Windows.Data.Binding("ScaleX") { Source = clip, Mode = System.Windows.Data.BindingMode.TwoWay });
        var input = typeof(VideoWallRW.App.Controls.ValueSlider).GetMethod("ApplyDisplayInput", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        input.Invoke(slider, new object[] { "960" });
        Near(clip.ScaleX, .5, "Pixel input -> model scale");
        clip.ScaleX = 1.5;
        Near(slider.Value * slider.DisplayMultiplier, 2880, "Gizmo/model scale -> displayed pixels");
        input.Invoke(slider, new object[] { "1005,5" });
        Near(clip.ScaleX * 1920, 1005.5, "Decimal comma input");
        input.Invoke(slider, new object[] { "NaN" });
        Near(clip.ScaleX * 1920, 1005.5, "Reject non-finite input");
        input.Invoke(slider, new object[] { "999999" });
        Near(clip.ScaleX, 8, "Maximum scale preserved");
        slider.DisplayMultiplier = 1005;
        Near(slider.Value * slider.DisplayMultiplier, 8040, "Changing resize basis preserves scale");
        Console.WriteLine("Dimension checks passed: fit/fill/stretch/original, pixel input, two-way binding, decimal comma and limits.");
    }}




