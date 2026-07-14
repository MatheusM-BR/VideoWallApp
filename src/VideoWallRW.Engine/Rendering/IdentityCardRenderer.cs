using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using VideoWallRW.Engine.Graphics;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace VideoWallRW.Engine.Rendering;

/// <summary>
/// Gera (e cacheia) o cartão de identificação de uma saída: fundo colorido + nome da
/// tela, device e resolução em texto grande. Renderizado uma única vez via GDI+
/// (não por frame — texto em D3D11 puro exigiria Direct2D/DirectWrite compartilhando
/// a textura; GDI+ para uma textura estática e cacheada é bem mais simples e o
/// resultado visual é o mesmo, já que isto nunca precisa animar).
/// </summary>
public sealed class IdentityCardRenderer : IDisposable
{
    private readonly GraphicsDevice _gfx;
    private readonly Dictionary<string, (ID3D11Texture2D Tex, ID3D11ShaderResourceView Srv)> _cache = [];

    public IdentityCardRenderer(GraphicsDevice gfx) => _gfx = gfx;

    public ID3D11ShaderResourceView GetOrCreate(string monitorDeviceName, string screenName, int sliceCount, int width, int height)
    {
        string key = $"{monitorDeviceName}|{width}x{height}|{screenName}|{sliceCount}";
        if (_cache.TryGetValue(key, out var existing))
            return existing.Srv;

        var (pixels, stride) = RenderCardPixels(screenName, monitorDeviceName, sliceCount, width, height);

        var texDesc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Immutable,
            BindFlags = BindFlags.ShaderResource,
        };

        ID3D11Texture2D tex;
        unsafe
        {
            fixed (byte* p = pixels)
            {
                var data = new SubresourceData((nint)p, (uint)stride, (uint)pixels.Length);
                tex = _gfx.Device.CreateTexture2D(texDesc, [data]);
            }
        }
        var srv = _gfx.Device.CreateShaderResourceView(tex);
        _cache[key] = (tex, srv);
        return srv;
    }

    private static (byte[] Pixels, int Stride) RenderCardPixels(
        string screenName, string deviceName, int sliceCount, int width, int height)
    {
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            // Cor de fundo derivada do nome do device — cada tela fica com um tom distinto.
            int hash = deviceName.GetHashCode();
            var bg = Color.FromArgb(255, 40 + System.Math.Abs(hash) % 120, 60, 140);
            g.Clear(bg);

            using var borderPen = new Pen(Color.White, System.Math.Max(2, height / 100f));
            g.DrawRectangle(borderPen, borderPen.Width, borderPen.Width,
                width - borderPen.Width * 2, height - borderPen.Width * 2);

            var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using var brush = new SolidBrush(Color.White);

            using var fontBig = new Font("Segoe UI", height / 9f, FontStyle.Bold);
            g.DrawString(screenName, fontBig, brush, new RectangleF(0, height * 0.18f, width, height * 0.3f), center);

            using var fontMed = new Font("Segoe UI", height / 20f);
            string info = $"{deviceName}\n{width}×{height}" + (sliceCount > 1 ? $"\n{sliceCount} slices" : "");
            g.DrawString(info, fontMed, brush, new RectangleF(0, height * 0.5f, width, height * 0.4f), center);
        }

        var rect = new Rectangle(0, 0, width, height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int size = data.Stride * height;
            byte[] pixels = new byte[size];
            Marshal.Copy(data.Scan0, pixels, 0, size);
            return (pixels, data.Stride);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    public void Dispose()
    {
        foreach (var (tex, srv) in _cache.Values)
        {
            srv.Dispose();
            tex.Dispose();
        }
        _cache.Clear();
    }
}
