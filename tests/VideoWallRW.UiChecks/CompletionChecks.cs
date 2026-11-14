using System.Dynamic;
using System.IO;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using VideoWallRW.App;
using VideoWallRW.App.Controls;
using VideoWallRW.App.ViewModels;
using VideoWallRW.Core.Model;

internal static class CompletionChecks
{
    static void Assert(bool condition, string label) { if (!condition) throw new Exception(label); }
    static void Frame() => Dispatcher.CurrentDispatcher.Invoke(() => {}, DispatcherPriority.ApplicationIdle);
    static IEnumerable<DependencyObject> Children(DependencyObject parent)
    {
        for (int i=0; i<VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child=VisualTreeHelper.GetChild(parent,i); yield return child;
            foreach(var nested in Children(child)) yield return nested;
        }
    }
    static dynamic Obj(params (string,object?)[] items)
    {
        IDictionary<string,object?> result=new ExpandoObject();
        foreach(var (name,value) in items) result[name]=value;
        return (ExpandoObject)result;
    }
    static void Save(FrameworkElement root,string name,double scale=1)
    {
        root.UpdateLayout(); Frame();
        var visual=new DrawingVisual();
        using(var dc=visual.RenderOpen()) dc.DrawRectangle(new VisualBrush(root),null,new Rect(0,0,root.ActualWidth,root.ActualHeight));
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth*scale),(int)Math.Ceiling(root.ActualHeight*scale),96*scale,96*scale,PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream=File.Create($"artifacts/ui-review/completion-{name}.png"); encoder.Save(stream);
    }
    public static void CheckDpi(FrameworkElement root)
    {
        foreach(var scale in new[]{1d,1.25,1.5,2d}) Save(root,"dpi-"+(int)(scale*100),scale);
        Console.WriteLine("Rendering checked at 100%, 125%, 150%, 200% pixel density; physical monitor DPI not changed.");
    }
    public static void CheckInteractions(Window main)
    {
        var model=new Clip(new FileImageSource("does-not-exist-test.png")) { InPoint=0,OutPoint=20,ImageDuration=30 };
        var clip=new ClipViewModel(model,0,0,null!,_=>{},_=>{}) { Duration=30 };
        clip.InPointText="abc"; clip.Volume=.42;
        Assert(clip.HasTimingError && Math.Abs(model.Volume-.42)<.001 && model.InPoint==0,"Invalid input must not block independent edits");
        clip.InPointText="8"; clip.OutPointText="5";
        Assert(clip.HasTimingError && model.OutPoint==20,"Reversed interval must not replace valid times");
        clip.OutPointText="18";
        Assert(!clip.HasTimingError && model.InPoint==8 && model.OutPoint==18,"Corrected interval must apply");
        clip.ImageDurationText="0"; clip.Name="Nome atualizado";
        Assert(clip.HasTimingError && model.ImageDuration==30 && model.Name=="Nome atualizado","Invalid duration must preserve model while other fields apply");
        clip.ImageDurationText="NaN"; Assert(clip.HasTimingError,"NaN duration rejected");
        clip.ImageDurationText="30"; clip.OutPointText="31"; Assert(clip.HasTimingError,"Past end rejected");
        clip.OutPointText="18";
        var input=new TextBox(); BindingOperations.SetBinding(input,TextBox.TextProperty,new Binding("InPointText") { Source=clip,Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged,ValidatesOnDataErrors=true });
        input.Text="inválido"; input.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
        Assert(Validation.GetHasError(input),"WPF field error must be visible");
        input.Text="3"; input.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
        Assert(!Validation.GetHasError(input),"Corrected WPF field error clears");
        var slider=new ValueSlider { DisplayMultiplier=1920,Unit=" px",Minimum=.05,Maximum=8,DefaultValue=1,InputTitle="Largura (px)" };
        BindingOperations.SetBinding(slider,ValueSlider.ValueProperty,new Binding("ScaleX") { Source=clip,Mode=BindingMode.TwoWay });
        var panel=new StackPanel(); panel.Children.Add(input); panel.Children.Add(slider);
        var host=new Window { Content=panel,Width=420,Height=220,Left=-20000,Top=-20000,ShowActivated=false,ShowInTaskbar=false };
        host.Show(); Frame();
        var key=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(slider),0,Key.Right) { RoutedEvent=Keyboard.KeyDownEvent };
        slider.RaiseEvent(key); Assert(key.Handled && Math.Abs(clip.ScaleX- (1+1d/1920))<.000001,"Right arrow adjusts one pixel through binding");
        var home=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(slider),0,Key.Home) { RoutedEvent=Keyboard.KeyDownEvent };
        slider.RaiseEvent(home); Assert(clip.ScaleX==1,"Home restores default");
        var peer=UIElementAutomationPeer.CreatePeerForElement(slider)!;
        var range=(IRangeValueProvider)peer.GetPattern(PatternInterface.RangeValue)!;
        range.SetValue(960); Assert(clip.ScaleX==.5 && range.Value==960,"Accessible range uses pixels");
        Assert(peer.GetName()=="Largura (px)","Accessible name exists");
        var gizmo=new TransformGizmo(); BindingOperations.SetBinding(gizmo,TransformGizmo.ScaleXProperty,new Binding("ScaleX") { Source=clip,Mode=BindingMode.TwoWay });
        gizmo.ScaleX=1.5; clip.ScaleX=2;
        Assert(gizmo.ScaleX==2 && BindingOperations.IsDataBound(gizmo,TransformGizmo.ScaleXProperty),"Gizmo edit must preserve two-way binding");
        host.Close();
        dynamic data=main.DataContext;
        dynamic selected=data.Grid.SelectedClip;
        foreach(dynamic item in data.Grid.Layers[0].Clips) item.IsActive=false;
        clip.IsActive=true; clip.SourceWidth=1920;clip.SourceHeight=1080;
        clip.ThumbnailPath=Path.GetFullPath("artifacts/ui-review/demo-clip-0.png");
        clip.InPointText="8";clip.OutPointText="18";
        data.Grid.SelectedClip=clip;main.Width=1440;main.Height=900;Frame();
        Save((FrameworkElement)main.Content,"active");
        clip.OutPointText="5";Frame();
        Assert(clip.TrimIn==8 && clip.TrimOut==18,"Invalid timing must preserve the displayed valid timeline");
        Assert(Children(main).OfType<TextBox>().Any(Validation.GetHasError),"Inspector must show timing errors on actual fields");
        Save((FrameworkElement)main.Content,"active-error");
        data.Grid.SelectedClip=selected;Frame();
        data.Blackout=true;Frame();Save((FrameworkElement)main.Content,"blackout");data.Blackout=false;
        data.Grid.SelectedClip=null;Frame();Save((FrameworkElement)main.Content,"empty");data.Grid.SelectedClip=selected;
        Console.WriteLine("UI interactions passed: field errors, independent edits, correction, keyboard, accessible range, bindings, active/blackout/empty states.");
    }
    static Window Fixture(string name,object data)
    {
        string x=File.ReadAllText($"src/VideoWallRW.App/{name}Window.xaml");
        x=Regex.Replace(x," x:Class=\"[^\"]*\"","");
        x=Regex.Replace(x,"\\s+\\w+=\"On[^\"]*\"","");
        x=x.Replace("clr-namespace:VideoWallRW.App.Controls","clr-namespace:VideoWallRW.App.Controls;assembly=VideoWallRW.App");
        var win=(Window)XamlReader.Parse(x);win.DataContext=data;win.Left=-20000;win.Top=-20000;win.ShowActivated=false;win.ShowInTaskbar=false;win.Show();Frame();return win;
    }
    public static void CheckAuxiliaryWindows()
    {
        var screen=new ScreenViewModel("Tela de demonstração",1920,1080,()=>{});
        screen.AddSlice(new Slice { Name="Recorte principal",InputRect=new(0,0,1920,1080),OutputRect=new(0,0,1920,1080) });
        dynamic outputs=Obj(("Screens",new[]{screen}),("SelectedScreen",screen),("CompWidth",3840),("CompHeight",1080),("Identify",false),("PresetNames",new[]{"Principal"}),("PresetName","Principal"),("Rotations",Enum.GetValues<VideoWallRW.Core.Model.Rotation>()),("AvailableMonitors",Array.Empty<object>()),("StatusText","Demonstração · nenhuma saída ativada"));
        var output=Fixture("OutputSetup",(object)outputs);Save((FrameworkElement)output.Content,"outputs");output.Width=1100;output.Height=700;Frame();Save((FrameworkElement)output.Content,"outputs-small");output.Close();
        dynamic install=Obj(("Steps",new[]{Obj(("Column",1),("Seconds",30d))}),("Rules",Array.Empty<object>()),("WatchFolders",Array.Empty<object>()),("AutostartEnabled",false),("SequencerEnabled",false),("SequencerLoop",true),("StatusText","Demonstração"));
        var installation=Fixture("Installation",(object)install);
        foreach(var tabs in Children(installation).OfType<TabControl>().ToList())
        for(int index=0;index<tabs.Items.Count;index++) {tabs.SelectedIndex=index;Frame();Save((FrameworkElement)installation.Content,"installation-"+index);}
        installation.Close();
        var keyOption=new LayerTargetOption(null,"Todas as camadas");
        dynamic keys=Obj(("Entries",new[]{Obj(("Action","ToggleMasterPause"),("Gesture","Ctrl+Espaço"),("IsCapturing",false),("HasConflict",false),("IsColumnAction",false),("LayerOptions",new[]{keyOption}), ("SelectedLayer",keyOption))}),("StatusText","Demonstração"));
        var keymap=Fixture("KeymapEditor",(object)keys);Save((FrameworkElement)keymap.Content,"keymap");keymap.Close();
        dynamic hap=Obj(("Files",new[]{Obj(("FileName","abertura.mp4"),("Progress",.65),("Status","Convertendo"))}),("Presets",new[]{"HAP"}),("SelectedPreset","HAP"),("DestinationFolder",@"C:\Video Wall\HAP"),("StatusText","Demonstração · nenhum arquivo convertido"));
        var converter=Fixture("HapConverter",(object)hap);Save((FrameworkElement)converter.Content,"hap");converter.Close();
        var help=new HelpWindow();help.Left=-20000;help.Top=-20000;help.ShowActivated=false;help.ShowInTaskbar=false;help.Show();Frame();Save((FrameworkElement)help.Content,"help");help.Close();
        var prompt=new TextPromptWindow("Largura na composição (px)","960") { Validator=_=>"Informe um número válido.",Left=-20000,Top=-20000,ShowActivated=false,ShowInTaskbar=false };
        prompt.Show();Frame();
        typeof(TextPromptWindow).GetMethod("OnOk",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.Invoke(prompt,new object[]{prompt,new RoutedEventArgs()});
        Assert(((TextBlock)prompt.FindName("ErrorMessage")).Visibility==Visibility.Visible,"Numeric prompt keeps error visible");
        Save((FrameworkElement)prompt.Content,"prompt-error");prompt.Close();
        var picker=Fixture("CellPicker",(object)Obj());Save((FrameworkElement)picker.Content,"cell-picker");picker.Close();
    }
}
