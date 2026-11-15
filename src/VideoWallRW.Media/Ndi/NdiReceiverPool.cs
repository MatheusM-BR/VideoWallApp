using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Serilog;

namespace VideoWallRW.Media.Ndi;

/// <summary>
/// Receptores NDI compartilhados e mantidos "quentes".
///
/// Por que existe (a demora pra abrir NDI): antes, cada disparo de uma célula NDI
/// criava um receptor NOVO — descoberta + conexão + espera do primeiro quadro, de
/// novo, toda vez. Com autopilot alternando imagem → câmera → imagem, a câmera levava
/// segundos pra entrar a cada volta. Agora o receptor fica aberto enquanto alguma
/// célula usa a fonte e por <see cref="Linger"/> depois que a última solta: voltar
/// pra câmera dentro desse tempo entra no próximo frame.
///
/// Também conecta pelo ENDEREÇO que o <see cref="NdiFinder"/> já descobriu (sem isso o
/// receptor refaz a descoberta mDNS por conta própria a cada conexão).
/// </summary>
public static class NdiReceiverPool
{
    /// <summary>Quanto tempo um receptor sem uso continua conectado.</summary>
    public static readonly TimeSpan Linger = TimeSpan.FromSeconds(90);

    private static readonly Dictionary<string, NdiReceiver> Receivers = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> KnownUrls = new(StringComparer.Ordinal);
    private static readonly object Gate = new();
    private static Timer? _sweeper;
    private static bool _shutdown;

    /// <summary>Endereço (url) anunciado pela fonte, alimentado pelo NdiFinder.</summary>
    internal static void RememberUrl(string name, string? url)
    {
        if (!string.IsNullOrEmpty(url)) KnownUrls[name] = url;
    }

    internal static string? UrlFor(string name) => KnownUrls.TryGetValue(name, out var url) ? url : null;

    /// <summary>Pega (ou cria) o receptor da fonte. Toda chamada precisa de um <see cref="Release"/>.</summary>
    internal static NdiReceiver Acquire(string sourceName)
    {
        lock (Gate)
        {
            if (_shutdown) throw new ObjectDisposedException(nameof(NdiReceiverPool));
            if (!Receivers.TryGetValue(sourceName, out var receiver) || receiver.IsDestroyed)
            {
                receiver = new NdiReceiver(sourceName, UrlFor(sourceName));
                Receivers[sourceName] = receiver;
            }
            receiver.AddRef();
            _sweeper ??= new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
            return receiver;
        }
    }

    internal static void Release(NdiReceiver receiver)
    {
        lock (Gate) receiver.ReleaseRef();
    }

    private static void Sweep()
    {
        List<NdiReceiver> expired;
        lock (Gate)
        {
            var now = DateTime.UtcNow;
            expired = Receivers.Values.Where(r => r.RefCount == 0 && now - r.IdleSinceUtc > Linger).ToList();
            foreach (var r in expired) Receivers.Remove(r.SourceName);
        }
        // Fecha fora do lock: destruir o receptor espera a thread de captura sair (até 2s).
        foreach (var r in expired)
        {
            Log.Debug("NDI: fechando receptor ocioso '{Source}'", r.SourceName);
            r.Destroy();
        }
    }

    /// <summary>Fecha tudo. Precisa rodar ANTES do NDIlib_destroy.</summary>
    internal static void Shutdown()
    {
        List<NdiReceiver> all;
        lock (Gate)
        {
            _shutdown = true;
            _sweeper?.Dispose();
            _sweeper = null;
            all = [.. Receivers.Values];
            Receivers.Clear();
        }
        foreach (var r in all) r.Destroy();
    }
}

/// <summary>
/// Uma conexão NDI com thread de captura própria. Guarda só o quadro mais recente:
/// a thread de captura preenche o buffer de trás e troca com o da frente sob lock;
/// quem consome copia o da frente (também sob lock), então nunca lê um quadro pela metade.
/// Vários consumidores (a mesma câmera em duas células) leem o mesmo quadro.
/// </summary>
internal sealed unsafe class NdiReceiver
{
    public string SourceName { get; }
    private IntPtr _recv;
    private readonly Thread? _thread;
    private volatile bool _running;

    private readonly object _frameLock = new();
    private byte[]? _front, _back;
    private int _width, _height, _stride;
    private long _sequence; // incrementa a cada quadro novo publicado

    public int RefCount { get; private set; }
    public DateTime IdleSinceUtc { get; private set; } = DateTime.UtcNow;
    public bool IsDestroyed { get; private set; }

    /// <summary>Erro na última leitura e nenhum quadro desde então (rede caiu etc.).</summary>
    public volatile string? Error;
    public double Fps { get; private set; }
    public bool HasFrame => Interlocked.Read(ref _sequence) > 0;

    public NdiReceiver(string sourceName, string? url)
    {
        SourceName = sourceName;
        // NDI usa UTF-8 nos nomes. StringToHGlobalAnsi (antes) corrompia nomes com
        // acento — "PC-ESTÚDIO (Câmera 1)" não batia com nenhuma fonte e o receptor
        // ficava esperando pra sempre, sem erro nenhum.
        IntPtr namePtr = Marshal.StringToCoTaskMemUTF8(sourceName);
        IntPtr urlPtr = url is null ? IntPtr.Zero : Marshal.StringToCoTaskMemUTF8(url);
        IntPtr recvNamePtr = Marshal.StringToCoTaskMemUTF8("VideoWallRW");
        try
        {
            var settings = new NdiInterop.recv_create_v3_t
            {
                source_to_connect_to = new NdiInterop.source_t { p_ndi_name = namePtr, p_url_address = urlPtr },
                color_format = NdiInterop.recv_color_format_e.BGRX_BGRA,
                bandwidth = NdiInterop.recv_bandwidth_e.highest,
                allow_video_fields = false,
                p_ndi_recv_name = recvNamePtr,
            };
            _recv = NdiInterop.NDIlib_recv_create_v3(&settings);
        }
        finally
        {
            Marshal.FreeCoTaskMem(namePtr);
            if (urlPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(urlPtr);
            Marshal.FreeCoTaskMem(recvNamePtr);
        }

        if (_recv == IntPtr.Zero)
        {
            Error = $"Não foi possível conectar na fonte NDI '{sourceName}'.";
            Log.Error("NDIlib_recv_create_v3 falhou para '{Source}'", sourceName);
            return;
        }

        Log.Information("NDI: receptor aberto para '{Source}' ({How})", sourceName,
            url is null ? "por nome" : $"endereço {url}");
        _running = true;
        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "NdiRecv:" + sourceName };
        _thread.Start();
    }

    public void AddRef()
    {
        RefCount++;
        if (RefCount == 1) SetTally(true);
    }

    public void ReleaseRef()
    {
        if (RefCount == 0) return;
        RefCount--;
        if (RefCount == 0)
        {
            IdleSinceUtc = DateTime.UtcNow;
            // Ocioso: a câmera não está mais no telão — apaga o tally (luz vermelha).
            SetTally(false);
        }
    }

    /// <summary>
    /// Sem tally "on program", muitos encoders em hardware (PTZ, HX) mandam o vídeo
    /// numa taxa de economia (~1 fps) — um vídeo wall que está usando a fonte está
    /// sempre no ar. Foi o motivo do "1 fps" na câmera PTZ real testada.
    /// </summary>
    private void SetTally(bool onAir)
    {
        if (_recv == IntPtr.Zero || IsDestroyed) return;
        var tally = new NdiInterop.tally_t { on_program = onAir, on_preview = onAir };
        NdiInterop.NDIlib_recv_set_tally(_recv, &tally);
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
            var type = NdiInterop.NDIlib_recv_capture_v3(_recv, &frame, IntPtr.Zero, IntPtr.Zero, 500);
            if (!_running) break;

            if (type == NdiInterop.frame_type_e.error)
            {
                Error = $"Erro recebendo da fonte NDI '{SourceName}' (fonte desconectada?).";
                continue;
            }
            if (type != NdiInterop.frame_type_e.video)
                continue; // timeout ou outro tipo de frame (áudio/metadata) — ignora

            try
            {
                int w = frame.xres, h = frame.yres;
                int stride = frame.line_stride_in_bytes > 0 ? frame.line_stride_in_bytes : w * 4;
                int size = stride * h;

                // Buffers reaproveitados: um quadro 4K (~33MB) alocado do zero a cada
                // captura ia pro Large Object Heap e as pausas do GC derrubavam a taxa.
                if (_back is null || _back.Length != size) _back = new byte[size];
                Marshal.Copy(frame.p_data, _back, 0, size);

                lock (_frameLock)
                {
                    (_front, _back) = (_back, _front);
                    _width = w;
                    _height = h;
                    _stride = stride;
                    _sequence++;
                }
                Error = null; // voltou a chegar quadro: a fonte se recuperou sozinha

                framesSinceCheck++;
                var now = DateTime.UtcNow;
                var elapsed = (now - lastFpsCheck).TotalSeconds;
                if (elapsed >= 1.0)
                {
                    Fps = framesSinceCheck / elapsed;
                    framesSinceCheck = 0;
                    lastFpsCheck = now;
                }

                // Prévia da célula a cada 30s (só enquanto alguém está usando a fonte).
                if (RefCount > 0 && now >= nextSnapshot)
                {
                    nextSnapshot = now.AddSeconds(30);
                    byte[] snap;
                    lock (_frameLock) snap = (byte[])_front!.Clone();
                    try { NdiSnapshotHub.Publish(SourceName, snap, w, h, stride); }
                    catch { /* nunca deixar a prévia derrubar a captura */ }
                }
            }
            finally
            {
                NdiInterop.NDIlib_recv_free_video_v2(_recv, &frame);
            }
        }
    }

    /// <summary>
    /// Se houver quadro mais novo que <paramref name="lastSequence"/>, entrega ele ao
    /// <paramref name="consume"/> DENTRO do lock (o buffer não pode ser trocado durante
    /// a cópia) e atualiza a sequência. Devolve false se não havia nada novo.
    /// </summary>
    public bool TryConsumeLatest(ref long lastSequence, FrameConsumer consume)
    {
        lock (_frameLock)
        {
            if (_front is null || _sequence == lastSequence) return false;
            consume(_front, _width, _height, _stride);
            lastSequence = _sequence;
            return true;
        }
    }

    public delegate void FrameConsumer(byte[] pixels, int width, int height, int stride);

    public void Destroy()
    {
        if (IsDestroyed) return;
        IsDestroyed = true;
        _running = false;
        _thread?.Join(TimeSpan.FromSeconds(2));
        if (_recv != IntPtr.Zero)
        {
            NdiInterop.NDIlib_recv_destroy(_recv);
            _recv = IntPtr.Zero;
        }
    }
}
