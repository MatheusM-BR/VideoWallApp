using System.Runtime.InteropServices;
using Serilog;
using VideoWallRW.Engine;
using VideoWallRW.Engine.Graphics;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace VideoWallRW.Media.Ndi;

/// <summary>
/// Fonte de vídeo NDI (rede): conecta num receptor pelo nome anunciado pela fonte
/// (ex.: "PC-ESTUDIO (Câmera 1)") e recebe frames continuamente numa thread própria.
/// Como é uma fonte AO VIVO (sem arquivo, sem duração), Seek/Duration não fazem
/// sentido — o clip sempre mostra o que está chegando da rede agora.
/// O upload pra textura (Map/Unmap no contexto D3D11) só acontece em
/// <see cref="AcquireFrame"/>, chamado exclusivamente pela render thread — a thread
/// de captura NDI só copia bytes pra um buffer gerenciado, nunca toca no D3D11
/// (mesma regra que VideoFileSource/HapVideoSource já seguem).
/// </summary>
public sealed unsafe class NdiVideoSource : IPlayableSource
{
    private readonly GraphicsDevice _gfx;
    private readonly string _sourceName;
    private IntPtr _recv;
    private Thread? _captureThread;
    private volatile bool _running;

    // Double buffer reaproveitado entre frames — sem isso, um frame 4K (~33MB em BGRA)
    // alocado do zero a cada captura ia direto pro Large Object Heap do GC e as pausas
    // de coleta deixavam a recepção "travada" (foi o que o operador viu na prática:
    // 0,8 fps numa fonte HX2 4K que deveria rodar bem mais fluida).
    private readonly object _frameLock = new();
    private byte[]? _bufA, _bufB;
    private bool _pendingIsA;
    private int _pendingWidth, _pendingHeight, _pendingStride;
    private bool _hasPendingFrame;

    private ID3D11Texture2D? _texture;
    private ID3D11ShaderResourceView? _srv;
    private int _texWidth, _texHeight;

    private volatile SourceState _state = SourceState.Arming;
    private string? _error;

    public SourceState State => _state;
    public string? ErrorMessage => _error;
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
    public double DecodeFps { get; private set; }

#pragma warning disable CS0067 // fonte ao vivo nunca "termina" naturalmente
    public event Action? EndReached;
#pragma warning restore CS0067

    public NdiVideoSource(GraphicsDevice gfx, string sourceName)
    {
        _gfx = gfx;
        _sourceName = sourceName;

        IntPtr namePtr = Marshal.StringToHGlobalAnsi(sourceName);
        IntPtr recvNamePtr = Marshal.StringToHGlobalAnsi("VideoWallRW");
        try
        {
            var settings = new NdiInterop.recv_create_v3_t
            {
                source_to_connect_to = new NdiInterop.source_t { p_ndi_name = namePtr },
                color_format = NdiInterop.recv_color_format_e.BGRX_BGRA,
                bandwidth = NdiInterop.recv_bandwidth_e.highest,
                allow_video_fields = false,
                p_ndi_recv_name = recvNamePtr,
            };
            _recv = NdiInterop.NDIlib_recv_create_v3(&settings);
        }
        finally
        {
            Marshal.FreeHGlobal(namePtr);
            Marshal.FreeHGlobal(recvNamePtr);
        }

        if (_recv == IntPtr.Zero)
        {
            _state = SourceState.Error;
            _error = $"Não foi possível conectar na fonte NDI '{sourceName}'.";
            Log.Error("NDIlib_recv_create_v3 falhou para '{Source}'", sourceName);
            return;
        }

        // Sem isso, muitos encoders em hardware (câmeras PTZ, HX) tratam o receptor
        // como "só olhando" e mandam o vídeo numa taxa de economia de banda (~1 fps)
        // até verem alguém marcado como "on program" — um vídeo wall está sempre "on
        // program" (o que chega aqui vai pro telão), então avisamos isso assim que
        // conecta. Foi exatamente esse o motivo do "1 fps" na câmera PTZ real testada.
        var tally = new NdiInterop.tally_t { on_program = true, on_preview = true };
        NdiInterop.NDIlib_recv_set_tally(_recv, &tally);

        _running = true;
        _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "NdiRecv:" + sourceName };
        _captureThread.Start();
    }

    private void CaptureLoop()
    {
        var lastFpsCheck = DateTime.UtcNow;
        int framesSinceCheck = 0;
        // Primeira prévia logo aos ~2s (pra a célula mostrar algo rápido), depois a cada 30s.
        var nextSnapshot = DateTime.UtcNow.AddSeconds(2);

        while (_running)
        {
            NdiInterop.video_frame_v2_t frame;
            var type = NdiInterop.NDIlib_recv_capture_v3(_recv, &frame, IntPtr.Zero, IntPtr.Zero, 1000);
            if (!_running) break;

            if (type == NdiInterop.frame_type_e.error)
            {
                _state = SourceState.Error;
                _error = $"Erro recebendo da fonte NDI '{_sourceName}' (fonte desconectada?).";
                continue;
            }
            if (type != NdiInterop.frame_type_e.video)
                continue; // timeout ou outro tipo de frame (áudio/metadata) — ignora

            try
            {
                int w = frame.xres, h = frame.yres;
                int stride = frame.line_stride_in_bytes > 0 ? frame.line_stride_in_bytes : w * 4;
                int size = stride * h;

                // Escreve no buffer que NÃO está pendente de leitura (o outro pode ainda
                // estar sendo copiado pela render thread em AcquireFrame/UploadFrame).
                bool writeToA = !_pendingIsA;
                byte[] target;
                if (writeToA)
                {
                    if (_bufA is null || _bufA.Length != size) _bufA = new byte[size];
                    target = _bufA;
                }
                else
                {
                    if (_bufB is null || _bufB.Length != size) _bufB = new byte[size];
                    target = _bufB;
                }
                Marshal.Copy(frame.p_data, target, 0, size);

                lock (_frameLock)
                {
                    _pendingIsA = writeToA;
                    _pendingWidth = w;
                    _pendingHeight = h;
                    _pendingStride = stride;
                    _hasPendingFrame = true;
                }

                framesSinceCheck++;
                var now = DateTime.UtcNow;
                var elapsed = (now - lastFpsCheck).TotalSeconds;
                if (elapsed >= 1.0)
                {
                    DecodeFps = framesSinceCheck / elapsed;
                    framesSinceCheck = 0;
                    lastFpsCheck = now;
                }

                if (_state == SourceState.Arming)
                    _state = SourceState.Playing;

                // Prévia da célula a cada 30s: publica uma CÓPIA do frame atual (o app
                // gera o PNG/miniatura fora daqui). Cópia própria pra não brigar com o
                // double-buffer que a render thread lê.
                if (now >= nextSnapshot)
                {
                    nextSnapshot = now.AddSeconds(30);
                    var snap = new byte[size];
                    Array.Copy(target, snap, size);
                    try { NdiSnapshotHub.Publish(_sourceName, snap, w, h, stride); }
                    catch { /* nunca deixar a prévia derrubar a captura */ }
                }
            }
            finally
            {
                NdiInterop.NDIlib_recv_free_video_v2(_recv, &frame);
            }
        }
    }

    public void Start() { if (_state == SourceState.Ready) _state = SourceState.Playing; }
    public void Stop() { if (_state == SourceState.Playing) _state = SourceState.Ready; }
    public void Seek(double seconds) { /* fonte ao vivo: não aplicável */ }

    /// <summary>Render thread: sobe o frame pendente (se houver) pra textura e devolve a SRV atual.</summary>
    public ID3D11ShaderResourceView? AcquireFrame(double playheadSeconds)
    {
        if (_state == SourceState.Error) return null;

        byte[]? pixels; int w, h, stride;
        lock (_frameLock)
        {
            if (!_hasPendingFrame) return _srv;
            pixels = _pendingIsA ? _bufA : _bufB;
            w = _pendingWidth; h = _pendingHeight; stride = _pendingStride;
            _hasPendingFrame = false;
        }
        if (pixels is null || w <= 0 || h <= 0) return _srv;

        EnsureTexture(w, h);
        UploadFrame(pixels, w, h, stride);
        Width = w;
        Height = h;
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
        _running = false;
        _captureThread?.Join(TimeSpan.FromSeconds(2));
        if (_recv != IntPtr.Zero)
        {
            NdiInterop.NDIlib_recv_destroy(_recv);
            _recv = IntPtr.Zero;
        }
        _srv?.Dispose();
        _texture?.Dispose();
        Log.Debug("NdiVideoSource descartado: {Source}", _sourceName);
    }
}
