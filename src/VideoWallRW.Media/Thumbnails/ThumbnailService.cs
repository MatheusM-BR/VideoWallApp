using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Serilog;
using VideoWallRW.Media.Decoding;
using Vortice.WIC;

namespace VideoWallRW.Media.Thumbnails;

/// <summary>
/// Serviço de geração assíncrona de miniaturas e cache local.
/// Suporta decodificação parcial via MediaDecoder (vídeos) e IWICImagingFactory (imagens).
/// A UI submete as requisições e assina ThumbReady para carregar as imagens sem congelar o Dispatcher.
/// </summary>
public sealed class ThumbnailService : IDisposable
{
    private readonly Channel<string> _queue;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, string> _activeRequests = new();
    private readonly string _cacheDir;

    public event Action<string, string>? ThumbReady; // (originalFilePath, thumbFilePath)

    public ThumbnailService()
    {
        _cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoWallRW", "thumbs");
        Directory.CreateDirectory(_cacheDir);

        _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = false });

        for (int i = 0; i < 2; i++)
        {
            Task.Run(WorkerLoop);
        }
    }

    public void RequestThumbnail(string path)
    {
        if (!File.Exists(path)) return;

        string hash = ComputeHash(path);
        string thumbPath = Path.Combine(_cacheDir, $"{hash}.png");

        if (File.Exists(thumbPath))
        {
            ThumbReady?.Invoke(path, thumbPath);
            return;
        }

        if (_activeRequests.TryAdd(path, thumbPath))
        {
            _queue.Writer.TryWrite(path);
        }
    }

    private async Task WorkerLoop()
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(_cts.Token))
            {
                while (_queue.Reader.TryRead(out string? path))
                {
                    if (path == null || _cts.IsCancellationRequested) continue;
                    
                    if (_activeRequests.TryGetValue(path, out string? thumbPath))
                    {
                        if (ProcessFile(path, thumbPath))
                        {
                            ThumbReady?.Invoke(path, thumbPath);
                        }
                        else
                        {
                            Log.Warning("Não foi possível gerar thumbnail para: {Path}", path);
                        }
                        _activeRequests.TryRemove(path, out _);
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* Ignorar */ }
    }

    private bool ProcessFile(string src, string dst)
    {
        string ext = Path.GetExtension(src).ToLowerInvariant();
        if (ext is ".mp4" or ".mkv" or ".avi" or ".mov" or ".webm")
            return GenerateFromVideo(src, dst);
        if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp")
            return GenerateFromImage(src, dst);
        
        return false;
    }

    private bool GenerateFromImage(string src, string dst)
    {
        try
        {
            using var factory = new IWICImagingFactory();
            using var decoder = factory.CreateDecoderFromFileName(src);
            using var frame = decoder.GetFrame(0);
            
            int originalW = (int)frame.Size.Width;
            int originalH = (int)frame.Size.Height;

            int dstH = 90;
            int dstW = (int)(originalW * ((double)dstH / originalH));

            using var scaler = factory.CreateBitmapScaler();
            scaler.Initialize(frame, (uint)dstW, (uint)dstH, BitmapInterpolationMode.HighQualityCubic);

            EncodeAndSave(factory, scaler, dst);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "WIC Thumbnail erro em: {Src}", src);
            return false;
        }
    }

    private bool GenerateFromVideo(string src, string dst)
    {
        try
        {
            using var decoder = MediaDecoder.Open(src);
            double seekTarget = decoder.Duration * 0.1;
            decoder.Seek(seekTarget);

            var frameTarget = new DecodedFrame();
            for (int i = 0; i < 30; i++) // Tenta ler até achar 1 frame
            {
                if (decoder.TryDecodeNext(frameTarget))
                {
                    SaveRgbaFrame(frameTarget, dst);
                    return true;
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "FFmpeg Thumbnail erro em: {Src}", src);
            return false;
        }
    }

    private unsafe void SaveRgbaFrame(DecodedFrame frame, string dst)
    {
        int originalW = frame.Width;
        int originalH = frame.Height;
        
        int dstH = 90;
        int dstW = (int)(originalW * ((double)dstH / originalH));

        using var factory = new IWICImagingFactory();
        
        fixed (byte* p = frame.Pixels)
        {
            using var bitmap = factory.CreateBitmapFromMemory(
                (uint)originalW, (uint)originalH, 
                PixelFormat.Format32bppPBGRA, 
                (uint)frame.Stride, 
                (uint)(frame.Height * frame.Stride), 
                (nint)p);

            using var scaler = factory.CreateBitmapScaler();
            scaler.Initialize(bitmap, (uint)dstW, (uint)dstH, BitmapInterpolationMode.HighQualityCubic);
            
            EncodeAndSave(factory, scaler, dst);
        }
    }

    private void EncodeAndSave(IWICImagingFactory factory, IWICBitmapSource source, string dst)
    {
        using var stream = factory.CreateStream();
        stream.Initialize(dst, FileAccess.Write);

        using var encoder = factory.CreateEncoder(ContainerFormat.Png);
        encoder.Initialize(stream);

        using var frameEncode = encoder.CreateNewFrame(out _);
        frameEncode.Initialize();
        frameEncode.WriteSource(source);
        frameEncode.Commit();
        encoder.Commit();
    }

    private string ComputeHash(string path)
    {
        var info = new FileInfo(path);
        string identifier = $"{path}_{info.LastWriteTimeUtc.Ticks}_{info.Length}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identifier));
        return Convert.ToHexString(hash).Substring(0, 16).ToLowerInvariant();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _queue.Writer.TryComplete();
    }
}
