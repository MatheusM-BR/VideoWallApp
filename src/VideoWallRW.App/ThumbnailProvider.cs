using System.Windows;
using VideoWallRW.Media.Thumbnails;

namespace VideoWallRW.App;

/// <summary>
/// Ponte entre a UI e o <see cref="ThumbnailService"/> (Media): as células da grade
/// pedem a miniatura pelo caminho da mídia e recebem o callback já na UI thread.
/// O callback é registrado ANTES de chamar o serviço porque, em cache hit, o
/// ThumbReady dispara sincronamente dentro do próprio Request.
/// </summary>
public static class ThumbnailProvider
{
    private static readonly Dictionary<string, List<Action<string>>> Pending = [];
    private static readonly object Gate = new();
    private static readonly ThumbnailService Service = Create();

    private static ThumbnailService Create()
    {
        var service = new ThumbnailService();
        service.ThumbReady += (sourcePath, thumbPath) =>
        {
            List<Action<string>>? callbacks;
            lock (Gate)
            {
                if (!Pending.Remove(sourcePath, out callbacks))
                    return;
            }
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                foreach (var callback in callbacks!)
                    callback(thumbPath);
            });
        };
        return service;
    }

    public static void Request(string mediaPath, Action<string> onReady)
    {
        lock (Gate)
        {
            if (Pending.TryGetValue(mediaPath, out var list))
                list.Add(onReady);
            else
                Pending[mediaPath] = [onReady];
        }
        Service.RequestThumbnail(mediaPath);
    }
}
