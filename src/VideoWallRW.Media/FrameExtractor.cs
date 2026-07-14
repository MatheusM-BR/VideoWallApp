using System.Security.Cryptography;
using System.Text;
using Serilog;
using VideoWallRW.Media.Decoding;
using Vortice.WIC;

namespace VideoWallRW.Media;

/// <summary>
/// Extrai um frame de um instante arbitrário do vídeo para PNG em cache — usado pelo
/// scrub da barra de trim (o operador vê o frame de In/Out enquanto arrasta a alça).
/// Cache por (arquivo, décimo de segundo) em %LocalAppData%\VideoWallRW\scrub-cache.
/// </summary>
public static class FrameExtractor
{
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VideoWallRW", "scrub-cache");

    public static string? TryExtractFrame(string path, double seconds)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            string key = Hash($"{path}|{seconds:0.0}");
            string dest = Path.Combine(CacheDir, $"{key}.png");
            if (File.Exists(dest))
                return dest;

            using var decoder = MediaDecoder.Open(path);
            decoder.Seek(Math.Clamp(seconds, 0, Math.Max(decoder.Duration - 0.05, 0)));

            var frame = new DecodedFrame();
            for (int i = 0; i < 60; i++) // decodifica até chegar num frame apresentável
            {
                if (decoder.TryDecodeNext(frame))
                {
                    SavePng(frame, dest);
                    return dest;
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Scrub frame falhou em {Path} @ {Seconds}s", path, seconds);
            return null;
        }
    }

    private static unsafe void SavePng(DecodedFrame frame, string dest)
    {
        // Reduz pra altura 270 (16:9 → 480px) — é um preview de scrub, não precisa de alta.
        int dstH = Math.Min(frame.Height, 270);
        int dstW = (int)(frame.Width * ((double)dstH / frame.Height));

        using var factory = new IWICImagingFactory();
        fixed (byte* p = frame.Pixels)
        {
            using var bitmap = factory.CreateBitmapFromMemory(
                (uint)frame.Width, (uint)frame.Height,
                PixelFormat.Format32bppPBGRA,
                (uint)frame.Stride,
                (uint)(frame.Height * frame.Stride),
                (nint)p);

            using var scaler = factory.CreateBitmapScaler();
            scaler.Initialize(bitmap, (uint)dstW, (uint)dstH, BitmapInterpolationMode.Linear);

            using var stream = factory.CreateStream();
            stream.Initialize(dest, FileAccess.Write);
            using var encoder = factory.CreateEncoder(ContainerFormat.Png);
            encoder.Initialize(stream);
            using var frameEncode = encoder.CreateNewFrame(out _);
            frameEncode.Initialize();
            frameEncode.WriteSource(scaler);
            frameEncode.Commit();
            encoder.Commit();
        }
    }

    private static string Hash(string s)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16].ToLowerInvariant();
}
