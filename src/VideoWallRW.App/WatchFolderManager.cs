using System.IO;
using System.Windows.Threading;
using Serilog;
using VideoWallRW.App.ViewModels;
using VideoWallRW.Core.Model;

namespace VideoWallRW.App;

/// <summary>
/// Fase 8 T8.6: observa pastas configuradas e injeta arquivos novos como clipes na
/// layer configurada, sem intervenção do operador (ex.: pasta de rede alimentada por
/// outro sistema). Debounce de 2s + checagem de acesso exclusivo evitam pegar um
/// arquivo ainda sendo copiado.
/// </summary>
public sealed class WatchFolderManager : IDisposable
{
    private static readonly string[] SupportedExtensions =
        [".mp4", ".mov", ".mkv", ".avi", ".wmv", ".webm", ".png", ".jpg", ".jpeg", ".bmp"];

    private readonly CompositionViewModel _grid;
    private readonly Dispatcher _dispatcher;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Dictionary<string, DispatcherTimer> _pending = [];

    public WatchFolderManager(CompositionViewModel grid, Dispatcher dispatcher)
    {
        _grid = grid;
        _dispatcher = dispatcher;
    }

    public void Apply(IReadOnlyList<WatchFolderConfig> folders)
    {
        Clear();
        foreach (var folder in folders)
        {
            if (!folder.Enabled || !Directory.Exists(folder.Path))
                continue;

            try
            {
                var watcher = new FileSystemWatcher(folder.Path)
                {
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true,
                };
                int layerIndex = folder.LayerIndex;
                watcher.Created += (_, e) => OnFileDetected(e.FullPath, layerIndex);
                watcher.Renamed += (_, e) => OnFileDetected(e.FullPath, layerIndex);
                _watchers.Add(watcher);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Falha ao observar pasta {Path}", folder.Path);
            }
        }
    }

    private void OnFileDetected(string path, int layerIndex)
    {
        if (!SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
            return;

        _dispatcher.BeginInvoke(() =>
        {
            if (_pending.TryGetValue(path, out var existing))
                existing.Stop();

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _pending.Remove(path);
                TryImport(path, layerIndex);
            };
            _pending[path] = timer;
            timer.Start();
        });
    }

    private void TryImport(string path, int layerIndex)
    {
        if (!IsReadable(path))
        {
            // Ainda sendo copiado — tenta de novo em 2s (debounce se reinicia sozinho
            // se um novo evento chegar; aqui é só um retry manual).
            _dispatcher.BeginInvoke(async () =>
            {
                await Task.Delay(2000);
                if (IsReadable(path))
                    _grid.AddFileAutoColumn(layerIndex, path);
            });
            return;
        }
        _grid.AddFileAutoColumn(layerIndex, path);
    }

    private static bool IsReadable(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    public void Clear()
    {
        foreach (var watcher in _watchers)
            watcher.Dispose();
        _watchers.Clear();
        foreach (var timer in _pending.Values)
            timer.Stop();
        _pending.Clear();
    }

    public void Dispose() => Clear();
}
