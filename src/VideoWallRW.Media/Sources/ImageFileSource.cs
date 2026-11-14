using Serilog;
using VideoWallRW.Engine;
using VideoWallRW.Engine.Graphics;
using Vortice.Direct3D11;
using Vortice.WIC;
using Vortice.DXGI;

namespace VideoWallRW.Media.Sources;

/// <summary>
/// Fonte de imagem estática. Decodifica via WIC de forma SÍNCRONA no construtor —
/// chamado pela thread única de criação de fontes do RenderThread (ClipCreation),
/// nunca por Task.Run paralelo (evita corrida de init nativa que corrompia o heap).
/// Imagens muito grandes (ex.: páginas de PDF 2274×4042) são REDUZIDAS antes de subir
/// pra GPU: um telão exibe no máximo a resolução da composição, então guardar 37 MB
/// de textura por foto só esgota a VRAM (crítico num GTX 1060 6GB) — provável causa
/// dos fechamentos. O cap mantém nitidez de sobra pra qualquer tela + zoom.
/// </summary>
public sealed class ImageFileSource : IPlayableSource
{
    /// <summary>Maior dimensão (px) que uma imagem pode ter na GPU. 2560 cobre com
    /// folga qualquer tela (até 1920) com margem de zoom, e corta a VRAM de imagens
    /// gigantes (2274×4042 → 1441×2560 ≈ 15 MB em vez de 37 MB).</summary>
    private const int MaxDimension = 2560;

    private ID3D11Texture2D? _texture;
    private ID3D11ShaderResourceView? _srv;

    private volatile SourceState _state = SourceState.Arming;
    private volatile string? _error;

    private int _width;
    private int _height;
    private bool _endReachedFired;

    public double Duration { get; set; } = 10.0;

    public double DecodeFps => 0; // Estático
    public SourceState State => _state;
    public string? ErrorMessage => _error;
    public int Width => _width;
    public int Height => _height;

    public bool SupportsSeek => false;
    public event Action? EndReached;

    public ImageFileSource(GraphicsDevice gfx, string path)
    {
        try
        {
            Log.Debug("Carregando imagem {Path}", path);

            using var factory = new IWICImagingFactory();
            using var decoder = factory.CreateDecoderFromFileName(path);
            using var frame = decoder.GetFrame(0);

            int srcW = (int)frame.Size.Width;
            int srcH = (int)frame.Size.Height;
            // Width/Height reportados são SEMPRE os NATIVOS do arquivo, mesmo quando a
            // textura é reduzida: o engine usa esses valores no ResizeMath (o modo
            // "Original" posiciona pelo tamanho em pixel de verdade) e o preview do
            // painel TRANSFORMAR usa as dimensões sondadas do arquivo. Reportar o
            // tamanho reduzido faria o telão e o preview discordarem. O mapeamento de
            // UV não depende disso (amostra a textura inteira, 0..1), então a redução
            // segue invisível pro resto do sistema.
            _width = srcW;
            _height = srcH;

            using var converter = factory.CreateFormatConverter();
            converter.Initialize(frame, PixelFormat.Format32bppPBGRA, BitmapDitherType.None, null, 0.0, BitmapPaletteType.Custom);

            // Downscale se exceder o cap — a maior dimensão vira MaxDimension e a outra
            // acompanha a proporção. IWICBitmapScaler faz isso no próprio WIC (rápido).
            IWICBitmapSource pixelSource = converter;
            IWICBitmapScaler? scaler = null;
            // texW/texH = tamanho FÍSICO da textura (pode ser reduzido);
            // _width/_height = tamanho NATIVO reportado ao engine (nunca muda).
            int texW = srcW, texH = srcH;
            if (srcW > MaxDimension || srcH > MaxDimension)
            {
                double f = MaxDimension / (double)Math.Max(srcW, srcH);
                texW = Math.Max(1, (int)Math.Round(srcW * f));
                texH = Math.Max(1, (int)Math.Round(srcH * f));
                scaler = factory.CreateBitmapScaler();
                scaler.Initialize(converter, (uint)texW, (uint)texH, BitmapInterpolationMode.Fant);
                pixelSource = scaler;
                Log.Debug("Imagem grande reduzida: {Path} {SW}x{SH} → {W}x{H} (nativo reportado ao engine)",
                    path, srcW, srcH, texW, texH);
            }

            int stride = texW * 4;
            int bufferSize = stride * texH;
            byte[] pixels = new byte[bufferSize];
            pixelSource.CopyPixels((uint)stride, pixels);
            scaler?.Dispose();

            var desc = new Texture2DDescription
            {
                Width = (uint)texW,
                Height = (uint)texH,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Immutable,
                BindFlags = BindFlags.ShaderResource
            };

            unsafe
            {
                fixed (byte* p = pixels)
                {
                    var data = new Vortice.Direct3D11.SubresourceData((nint)p, (uint)stride, (uint)bufferSize);
                    _texture = gfx.Device.CreateTexture2D(desc, [data]);
                }
            }
            _srv = gfx.Device.CreateShaderResourceView(_texture);

            _state = SourceState.Playing;
            Log.Information("Imagem carregada: {Path} ({W}x{H})", path, _width, _height);
        }
        catch (Exception ex)
        {
            _srv?.Dispose();
            _texture?.Dispose();
            _srv = null;
            _texture = null;
            _error = ex.Message;
            _state = SourceState.Error;
            Log.Error(ex, "Falha ao decodificar imagem {Path}", path);
        }
    }

    public ID3D11ShaderResourceView? AcquireFrame(double playheadSeconds)
    {
        if (_state == SourceState.Error) return null;

        if (!_endReachedFired && _state == SourceState.Playing && playheadSeconds >= Duration)
        {
            _endReachedFired = true;
            EndReached?.Invoke();
        }

        return _srv;
    }

    public void Seek(double seconds) {}
    public void Start() {}
    public void Stop() {}

    public void Dispose()
    {
        _srv?.Dispose();
        _texture?.Dispose();
        _srv = null;
        _texture = null;
    }
}
