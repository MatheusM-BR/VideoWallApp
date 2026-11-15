using Serilog;
using VideoWallRW.Engine;
using VideoWallRW.Engine.Graphics;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace VideoWallRW.Media.Ndi;

/// <summary>
/// Fonte de vídeo NDI (rede) numa célula. A conexão em si mora no
/// <see cref="NdiReceiverPool"/>: é compartilhada entre células com a mesma fonte e
/// continua aberta um tempo depois que a célula sai do ar — redisparar a câmera entra
/// no próximo frame, sem reconectar.
/// Como é uma fonte AO VIVO (sem arquivo, sem duração), Seek/Duration não fazem
/// sentido — o clip sempre mostra o que está chegando da rede agora.
/// O upload pra textura (Map/Unmap no contexto D3D11) só acontece em
/// <see cref="AcquireFrame"/>, chamado exclusivamente pela render thread.
/// </summary>
public sealed unsafe class NdiVideoSource : IPlayableSource
{
    private readonly GraphicsDevice _gfx;
    private readonly string _sourceName;
    private NdiReceiver? _receiver;
    private long _lastSequence;

    private ID3D11Texture2D? _texture;
    private ID3D11ShaderResourceView? _srv;
    private int _texWidth, _texHeight;
    private bool _stopped;

    public SourceState State
    {
        get
        {
            if (_receiver is null || _receiver.IsDestroyed) return SourceState.Error;
            // Erro só conta enquanto nada chegou: a fonte que caiu e voltou se recupera sozinha.
            if (_receiver.Error is not null && !_receiver.HasFrame) return SourceState.Error;
            if (!_receiver.HasFrame) return SourceState.Arming;
            return _stopped ? SourceState.Ready : SourceState.Playing;
        }
    }

    public string? ErrorMessage => _receiver?.Error ?? (_receiver is null ? $"Fonte NDI '{_sourceName}' indisponível." : null);
    public int Width { get; private set; }
    public int Height { get; private set; }
    // Fonte ao vivo: NUNCA "termina". Duration=0 parecia o valor óbvio, mas
    // ClipPlayer.GetBounds() usa Math.Max(0, Duration) como outPoint do loop — com
    // outPoint=0 o playhead já passa dele no primeiro Tick, e PlayMode.Loop dispara
    // RaiseEndReached() (autopilot) TODO tick, o que reconectava/retriggerava o clip
    // dezenas de vezes por segundo e derrubava o app inteiro pra ~1 fps (era isso, não
    // o bandwidth do NDI). PositiveInfinity garante que o playhead nunca alcança o
    // outPoint, então o Loop nunca "fecha a volta" — exatamente o comportamento certo
    // pra uma câmera ao vivo.
    public double Duration => double.PositiveInfinity;
    public bool SupportsSeek => false;
    public double DecodeFps => _receiver?.Fps ?? 0;

#pragma warning disable CS0067 // fonte ao vivo nunca "termina" naturalmente
    public event Action? EndReached;
#pragma warning restore CS0067

    public NdiVideoSource(GraphicsDevice gfx, string sourceName)
    {
        _gfx = gfx;
        _sourceName = sourceName;
        try
        {
            _receiver = NdiReceiverPool.Acquire(sourceName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Falha ao abrir a fonte NDI '{Source}'", sourceName);
        }
    }

    public void Start() => _stopped = false;
    public void Stop() => _stopped = true;
    public void Seek(double seconds) { /* fonte ao vivo: não aplicável */ }

    /// <summary>Render thread: sobe o quadro mais novo (se houver) pra textura e devolve a SRV atual.</summary>
    public ID3D11ShaderResourceView? AcquireFrame(double playheadSeconds)
    {
        if (_receiver is null) return null;
        _receiver.TryConsumeLatest(ref _lastSequence, (pixels, w, h, stride) =>
        {
            if (w <= 0 || h <= 0) return;
            EnsureTexture(w, h);
            UploadFrame(pixels, w, h, stride);
            Width = w;
            Height = h;
        });
        return _srv;
    }

    private void EnsureTexture(int w, int h)
    {
        if (_texture is not null && _texWidth == w && _texHeight == h) return;

        _srv?.Dispose();
        _texture?.Dispose();

        var desc = new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.Write,
        };
        _texture = _gfx.Device.CreateTexture2D(desc);
        _srv = _gfx.Device.CreateShaderResourceView(_texture);
        _texWidth = w;
        _texHeight = h;
    }

    private void UploadFrame(byte[] pixels, int w, int h, int srcStride)
    {
        var mapped = _gfx.Context.Map(_texture!, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            fixed (byte* src = pixels)
            {
                byte* dst = (byte*)mapped.DataPointer;
                int dstStride = (int)mapped.RowPitch;
                int rowBytes = w * 4;
                for (int y = 0; y < h; y++)
                    System.Buffer.MemoryCopy(src + y * srcStride, dst + y * dstStride, rowBytes, rowBytes);
            }
        }
        finally
        {
            _gfx.Context.Unmap(_texture!, 0);
        }
    }

    public void Dispose()
    {
        if (_receiver is not null)
        {
            NdiReceiverPool.Release(_receiver);
            _receiver = null;
        }
        _srv?.Dispose();
        _texture?.Dispose();
        Log.Debug("NdiVideoSource descartado: {Source}", _sourceName);
    }
}
