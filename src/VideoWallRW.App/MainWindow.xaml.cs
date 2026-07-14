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

    // ── Painel FILES: duplo-clique envia, arrastar solta nas células (FileDrop) ──
    private Point _fileDragStart;

    private void OnFileListMouseDown(object sender, MouseButtonEventArgs e)
        => _fileDragStart = e.GetPosition(null);

    private void OnFileListMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _fileDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _fileDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        if (FileList.SelectedItem is ViewModels.FileBrowserItem { IsFolder: false } item)
            DragDrop.DoDragDrop(FileList,
                new DataObject(DataFormats.FileDrop, new[] { item.FullPath }), DragDropEffects.Copy);
    }

    private void OnFileListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileList.SelectedItem is ViewModels.FileBrowserItem item)
            _vm.FileBrowser.Open(item);
    }

    private void OnEditImageClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: ViewModels.ClipViewModel clipVm }) return;
        if (clipVm.Model?.Source is not Core.Model.FileImageSource img) return;
        _vm.EditCellImage(clipVm.LayerIndex, clipVm.ColumnIndex, img.Path);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+C/Ctrl+V fora de um TextBox: copiar/colar CLIP entre células.
        // Dentro de um TextBox o comportamento nativo de clipboard continua intacto.
        if (Keyboard.FocusedElement is not TextBoxBase && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.C && _vm.Grid.CopySelectedClip()) { e.Handled = true; return; }
            if (e.Key == Key.V && _vm.Grid.PasteClipboardClip()) { e.Handled = true; return; }
        }

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
