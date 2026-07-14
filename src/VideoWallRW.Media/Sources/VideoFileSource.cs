using Serilog;
using VideoWallRW.Engine;
using VideoWallRW.Engine.Graphics;
using VideoWallRW.Media.Audio;
using VideoWallRW.Media.Decoding;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace VideoWallRW.Media.Sources;

/// <summary>
/// Fonte de vídeo de arquivo: decodifica via FFmpeg e faz upload para duas
/// texturas D3D11 (double buffer), expondo sempre a SRV mais recente.
/// <see cref="AcquireFrame"/> é chamado exclusivamente pela render thread.
/// Se o arquivo tiver trilha de áudio e um <see cref="AudioEngine"/> tiver sido
/// informado, o áudio é decodificado e tocado — controlado via <see cref="IAudioControllable"/>.
/// </summary>
public sealed class VideoFileSource : IPlayableSource, IAudioControllable
{
    private readonly GraphicsDevice _gfx;
    private readonly FrameQueue _queue;
    private readonly VideoDecodeWorker _worker;
    private readonly AudioEngine? _audioEngine;
    private readonly ClipAudioTrack? _audioTrack;

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

    public VideoFileSource(GraphicsDevice gfx, string filePath, AudioEngine? audioEngine = null)
    {
        _gfx = gfx;
        _audioEngine = audioEngine;

        // Obter metadados sem iniciar a thread (abrir é rápido para arquivos locais).
        using var probe = MediaDecoder.Open(filePath);
        Width = probe.Width;
        Height = probe.Height;
        Duration = probe.Duration;

        if (probe.HasAudio && _audioEngine is not null)
        {
            _audioTrack = new ClipAudioTrack();
            _audioEngine.AddTrack(_audioTrack);
        }

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
        _worker = new VideoDecodeWorker(filePath, _queue,
            onAudioSamples: _audioTrack is null ? null : _audioTrack.Write,
            onAudioSeek: _audioTrack is null ? null : _audioTrack.Clear);
        _state = SourceState.Ready;

        Log.Debug("VideoFileSource criado: {File} {W}×{H} {Dur:F2}s", filePath, Width, Height, Duration);
    }

    // Preroll: só libera a reprodução com N frames no buffer — sem isso o clip entra
    // no ar com 1 frame decodificado e engasga nos primeiros instantes (e a cada
    // volta do loop, porque o seek esvazia a fila).
    private const int PrerollFrames = 3;
    private volatile bool _refilling = true;

    public void Start() => _state = SourceState.Playing;
    public void Stop() => _state = SourceState.Ready;

    public void Seek(double seconds)
    {
        // Congela o relógio do ClipPlayer (que pula Tick em Arming) enquanto a fila
        // reabastece — em vez de o playhead avançar sobre frames que ainda não
        // existem e "pular" quando eles chegam.
        _refilling = true;
        if (_state == SourceState.Playing)
            _state = SourceState.Arming;
        _worker.RequestSeek(seconds);
    }

    public double DecodeFps => _worker.DecodeFps;

    public void SetAudioState(bool playing, double volume, bool muted, double speed, double fade)
        => _audioTrack?.SetAudioState(playing, volume, muted, speed, fade);

    /// <summary>
    /// Render thread: retorna a SRV com o frame correto para <paramref name="playheadSeconds"/>.
    /// Se não há frame novo, repete a SRV anterior (pausa congela frame gratuitamente).
    /// </summary>
    public ID3D11ShaderResourceView? AcquireFrame(double playheadSeconds)
    {
        if (_refilling)
        {
            if (_queue.Count < PrerollFrames)
                return _lastSrv; // segurando: repete o frame atual (null antes do 1º = não desenha)
            _refilling = false;
            if (_state == SourceState.Arming)
                _state = SourceState.Playing; // libera o relógio do ClipPlayer
        }

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
        if (_audioTrack is not null)
            _audioEngine?.RemoveTrack(_audioTrack);
        foreach (var srv in _srvs) srv.Dispose();
        foreach (var tex in _textures) tex.Dispose();
        Log.Debug("VideoFileSource descartado.");
    }
}
