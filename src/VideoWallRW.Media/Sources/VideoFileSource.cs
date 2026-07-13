using Serilog;
using VideoWallRW.Engine;
using VideoWallRW.Engine.Graphics;
using VideoWallRW.Media.Decoding;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace VideoWallRW.Media.Sources;

/// <summary>
/// Fonte de vídeo de arquivo: decodifica via FFmpeg e faz upload para duas
/// texturas D3D11 (double buffer), expondo sempre a SRV mais recente.
/// <see cref="AcquireFrame"/> é chamado exclusivamente pela render thread.
/// </summary>
public sealed class VideoFileSource : IPlayableSource
{
    private readonly GraphicsDevice _gfx;
    private readonly FrameQueue _queue;
    private readonly VideoDecodeWorker _worker;

    // Double buffer: upload alterna entre tex[0] e tex[1].
    private readonly ID3D11Texture2D[] _textures = new ID3D11Texture2D[2];
    private readonly ID3D11ShaderResourceView[] _srvs = new ID3D11ShaderResourceView[2];
    private int _currentBuffer; // 0 ou 1; render thread usa _srvs[_currentBuffer]
    private ID3D11ShaderResourceView? _lastSrv;

    private volatile SourceState _state = SourceState.Arming;
    private string? _errorMessage = null;

    public SourceState State => _state;
    public string? ErrorMessage => _errorMessage;
    public int Width { get; }
    public int Height { get; }
    public double Duration { get; }
    public bool SupportsSeek => true;

#pragma warning disable CS0067 // EndReached disparado pelo worker em modos não-loop (fase 3)
    public event Action? EndReached;
#pragma warning restore CS0067

    public VideoFileSource(GraphicsDevice gfx, string filePath)
    {
        _gfx = gfx;

        // Obter metadados sem iniciar a thread (abrir é rápido para arquivos locais).
        using var probe = MediaDecoder.Open(filePath);
        Width = probe.Width;
        Height = probe.Height;
        Duration = probe.Duration;

        // Criar texturas Dynamic BGRA (upload pela CPU a cada frame).
        var texDesc = new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.Write,
        };
        for (int i = 0; i < 2; i++)
        {
            _textures[i] = gfx.Device.CreateTexture2D(texDesc);
            _srvs[i] = gfx.Device.CreateShaderResourceView(_textures[i]);
        }

        _queue = new FrameQueue();
        _worker = new VideoDecodeWorker(filePath, _queue);
        _state = SourceState.Ready;

        Log.Debug("VideoFileSource criado: {File} {W}×{H} {Dur:F2}s", filePath, Width, Height, Duration);
    }

    public void Start() => _state = SourceState.Playing;
    public void Stop() => _state = SourceState.Ready;

    public void Seek(double seconds) => _worker.RequestSeek(seconds);

    public double DecodeFps => _worker.DecodeFps;

    /// <summary>
    /// Render thread: retorna a SRV com o frame correto para <paramref name="playheadSeconds"/>.
    /// Se não há frame novo, repete a SRV anterior (pausa congela frame gratuitamente).
    /// </summary>
    public ID3D11ShaderResourceView? AcquireFrame(double playheadSeconds)
    {
        var frame = _queue.TryConsume(playheadSeconds);
        if (frame != null)
        {
            UploadFrame(frame);
            _queue.Return(frame);
            _currentBuffer ^= 1; // alterna buffer
            _lastSrv = _srvs[_currentBuffer ^ 1]; // o que acabou de ser escrito
        }
        // Se _lastSrv é null: ainda sem frame (Arming) — retornar null para não desenhar.
        return _lastSrv;
    }

    private unsafe void UploadFrame(DecodedFrame frame)
    {
        int buf = _currentBuffer;
        var mapped = _gfx.Context.Map(_textures[buf], 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            fixed (byte* src = frame.Pixels)
            {
                byte* dst = (byte*)mapped.DataPointer;
                int srcStride = frame.Stride;
                int dstStride = (int)mapped.RowPitch;
                int rowBytes = frame.Width * 4; // BGRA
                for (int y = 0; y < frame.Height; y++)
                    System.Buffer.MemoryCopy(src + y * srcStride, dst + y * dstStride, rowBytes, rowBytes);
            }
        }
        finally
        {
            _gfx.Context.Unmap(_textures[buf], 0);
        }
    }

    public void Dispose()
    {
        _worker.Dispose();
        _queue.Dispose();
        foreach (var srv in _srvs) srv.Dispose();
        foreach (var tex in _textures) tex.Dispose();
        Log.Debug("VideoFileSource descartado.");
    }
}
