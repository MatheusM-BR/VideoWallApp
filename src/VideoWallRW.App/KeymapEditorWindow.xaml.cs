using System.Windows;
using System.Windows.Input;
using VideoWallRW.App.ViewModels;

namespace VideoWallRW.App;

public partial class KeymapEditorWindow : Window
{
    private readonly KeymapEditorViewModel _vm;

    public KeymapEditorWindow(KeymapEditorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _vm = viewModel;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_vm.Entries.Any(x => x.IsCapturing))
            return;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            e.Handled = true;
            return;
        }

        var gesture = new KeyGesture(key, Keyboard.Modifiers);
        string text = new KeyGestureConverter().ConvertToInvariantString(gesture) ?? key.ToString();
        _vm.ApplyCapturedGesture(text);
        e.Handled = true;
    }
}
