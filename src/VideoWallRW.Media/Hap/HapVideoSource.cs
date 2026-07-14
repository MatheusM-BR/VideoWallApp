using Serilog;
using VideoWallRW.Engine;
using VideoWallRW.Engine.Graphics;
using VideoWallRW.Media.Decoding;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace VideoWallRW.Media.Hap;

/// <summary>
/// Fonte de vídeo HAP: demuxa + descomprime (Snappy) e faz upload direto dos bytes
/// DXT já comprimidos para uma textura BC1/BC3 — a GPU decodifica na amostragem,
/// sem passar por RGBA na CPU (é isso que torna HAP praticamente gratuito em CPU).
/// HapQ (YCoCg) não é suportado nesta versão — precisa de um passe de conversão de
/// cor no shader que ainda não existe; falha ao abrir com mensagem clara.
/// </summary>
public sealed class HapVideoSource : IPlayableSource
{
    private readonly GraphicsDevice _gfx;
    private readonly HapFrameQueue _queue;
    private readonly HapDecodeWorker _worker;
    private readonly Format _dxgiFormat;

    private readonly ID3D11Texture2D[] _textures = new ID3D11Texture2D[2];
    private readonly ID3D11ShaderResourceView[] _srvs = new ID3D11ShaderResourceView[2];
    private int _currentBuffer;
    private ID3D11ShaderResourceView? _lastSrv;

    private volatile SourceState _state = SourceState.Arming;

    public SourceState State => _state;
    public string? ErrorMessage { get; private set; }
    public int Width { get; }
    public int Height { get; }
    public double Duration { get; }
    public bool SupportsSeek => true;

#pragma warning disable CS0067 // interface exige o evento; HAP não sinaliza fim natural fora do loop
    public event Action? EndReached;
#pragma warning restore CS0067

    public HapVideoSource(GraphicsDevice gfx, string filePath)
    {
        _gfx = gfx;

        using var probe = HapDemuxer.Open(filePath);
        Width = probe.Width;
        Height = probe.Height;
        Duration = probe.Duration;

        var tagBytes = BitConverter.GetBytes(probe.CodecTag);
        string fourCc = new([(char)tagBytes[0], (char)tagBytes[1], (char)tagBytes[2], (char)tagBytes[3]]);
        _dxgiFormat = fourCc switch
        {
            "Hap1" => Format.BC1_UNorm,
            "Hap5" => Format.BC3_UNorm,
            "HapY" => throw new MediaOpenException(
                $"HapQ (YCoCg, '{fourCc}') não é suportado nesta versão — reexporte como Hap1 ou Hap5."),
            _ => throw new MediaOpenException($"FourCC HAP desconhecido: '{fourCc}'."),
        };
        var hapFormat = _dxgiFormat == Format.BC1_UNorm ? HapTextureFormat.Bc1 : HapTextureFormat.Bc3;

        var texDesc = new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = _dxgiFormat,
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

        _queue = new HapFrameQueue();
        _worker = new HapDecodeWorker(filePath, _queue);
        _state = SourceState.Ready;

        Log.Debug("HapVideoSource criado: {File} {W}×{H} {Dur:F2}s formato={Format}",
            filePath, Width, Height, Duration, hapFormat);
    }

    // Preroll (mesmo padrão do VideoFileSource): segura a entrada até N frames no
    // buffer e congela o relógio durante o refill do seek/loop.
    private const int PrerollFrames = 3;
    private volatile bool _refilling = true;

    public void Start() => _state = SourceState.Playing;
    public void Stop() => _state = SourceState.Ready;

    public void Seek(double seconds)
    {
        _refilling = true;
        if (_state == SourceState.Playing)
            _state = SourceState.Arming;
        _worker.RequestSeek(seconds);
    }

    public double DecodeFps => 0;

    public ID3D11ShaderResourceView? AcquireFrame(double playheadSeconds)
    {
        if (_refilling)
        {
            if (_queue.Count < PrerollFrames)
                return _lastSrv;
            _refilling = false;
            if (_state == SourceState.Arming)
                _state = SourceState.Playing;
        }

        var frame = _queue.TryConsume(playheadSeconds);
        if (frame != null)
        {
            UploadFrame(frame);
            _queue.Return(frame);
            _currentBuffer ^= 1;
            _lastSrv = _srvs[_currentBuffer ^ 1];
        }
        return _lastSrv;
    }

    private unsafe void UploadFrame(HapFrame frame)
    {
        int buf = _currentBuffer;
        var mapped = _gfx.Context.Map(_textures[buf], 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int blockBytes = frame.Format == HapTextureFormat.Bc1 ? 8 : 16;
            int blocksPerRow = (frame.Width + 3) / 4;
            int blockRows = (frame.Height + 3) / 4;
            int srcStride = blocksPerRow * blockBytes;
            int dstStride = (int)mapped.RowPitch;

            fixed (byte* src = frame.DxtBytes)
            {
                byte* dst = (byte*)mapped.DataPointer;
                for (int y = 0; y < blockRows; y++)
                    System.Buffer.MemoryCopy(src + y * srcStride, dst + y * dstStride, srcStride, srcStride);
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
        Log.Debug("HapVideoSource descartado.");
    }
}
