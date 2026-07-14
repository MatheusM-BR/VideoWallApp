using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using VideoWallRW.App.Input;

namespace VideoWallRW.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly KeymapService _keymap = new();

    public MainWindow(MainViewModel vm)
    {
        DataContext = vm;
        _vm = vm;
        InitializeComponent();

        _vm.KeymapChanged += () => _keymap.Reload();
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_vm.CanCloseWithoutPrompt())
        {
            e.Cancel = true;
            return;
        }
        _vm.OnClosing();
    }

    private void OnClipDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                if (sender is FrameworkElement fe && fe.DataContext is ViewModels.ClipViewModel clipVm)
                {
                    _vm.Grid.SetFiles(clipVm.LayerIndex, clipVm.ColumnIndex, files);
                    e.Handled = true;
                }
            }
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBoxBase || !_keymap.TryGetAction(e, out var action))
            return;

        if (action.StartsWith("TriggerColumn", StringComparison.Ordinal) &&
            int.TryParse(action["TriggerColumn".Length..], out int column))
        {
            _vm.Grid.TriggerColumnFromShortcut(column - 1);
        }
        else
        {
            switch (action)
            {
                case "ToggleMasterPause": _vm.ToggleMasterPause(); break;
                case "ClearLayer": _vm.Grid.ClearSelectedLayer(); break;
                case "ClearAll": _vm.Grid.ClearAll(); break;
                case "Blackout": _vm.ToggleBlackout(); break;
                case "ToggleOutputs": _vm.ToggleOutputs(); break;
                case "SaveProject": _vm.SaveProjectCommand.Execute(null); break;
                case "OpenProject": _vm.OpenProjectCommand.Execute(null); break;
                case "NewProject": _vm.NewProjectCommand.Execute(null); break;
                default: return;
            }
        }

        e.Handled = true;
    }
}
