using System.Diagnostics;
using Serilog;

namespace VideoWallRW.Media.Decoding;

/// <summary>
/// Thread de decodificação por clip. Produz frames para a <see cref="FrameQueue"/>
/// que a render thread consome. Usa generation counter para descartar frames
/// stale após seek.
/// </summary>
public sealed class VideoDecodeWorker : IDisposable
{
    private readonly string _filePath;
    private readonly FrameQueue _queue;
    private readonly Action<float[], int>? _onAudioSamples;
    private readonly Action? _onAudioSeek;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private int _generation; // Interlocked
    private volatile bool _seekPending;
    private double _seekTarget;

    // Estatísticas expostas para o snapshot da UI.
    private long _decodedFrames;
    private readonly Stopwatch _statsClock = Stopwatch.StartNew();
    private double _decodeFps;

    public double DecodeFps => _decodeFps;

    public VideoDecodeWorker(string filePath, FrameQueue queue,
        Action<float[], int>? onAudioSamples = null, Action? onAudioSeek = null)
    {
        _filePath = filePath;
        _queue = queue;
        _onAudioSamples = onAudioSamples;
        _onAudioSeek = onAudioSeek;
        _thread = new Thread(ThreadLoop) { Name = $"Decode:{Path.GetFileName(filePath)}", IsBackground = true };
        _thread.Start();
    }

    /// <summary>Solicita seek. Seguro chamar da render thread.</summary>
    public void RequestSeek(double seconds)
    {
        _seekTarget = seconds;
        Interlocked.Increment(ref _generation);
        _queue.Clear();
        _onAudioSeek?.Invoke();
        _seekPending = true;
    }

    private void ThreadLoop()
    {
        var ct = _cts.Token;
        MediaDecoder? decoder = null;
        try
        {
            decoder = MediaDecoder.Open(_filePath, _onAudioSamples, allowHardware: true);
            Log.Information("Decode worker iniciado: {File} (áudio: {HasAudio}, GPU: {Gpu})", _filePath, decoder.HasAudio, decoder.IsHardwareDecoding);
            double lastPts = 0;

            while (!ct.IsCancellationRequested)
            {
                // Processar seek pendente.
                if (_seekPending)
                {
                    _seekPending = false;
                    double target = _seekTarget;
                    decoder.Seek(target);

                    // Descartar frames até PTS >= target (seek preciso).
                    DiscardUntil(decoder, target, ct);
                    _queue.Clear(); // garantia extra
                }

                int gen = Volatile.Read(ref _generation);
                var frame = _queue.Rent();
                frame.Generation = gen;

                bool decoded;
                try
                {
                    decoded = decoder.TryDecodeNext(frame);
                }
                catch (MediaOpenException ex) when (decoder.IsHardwareDecoding)
                {
                    // A GPU falhou no meio do vídeo: reabre o MESMO arquivo no processador
                    // (o MediaDecoder já desligou a GPU pro resto da sessão) e continua de
                    // onde parou — o telão não perde o clipe.
                    Log.Warning(ex, "Decodificação pela GPU falhou em {File}; continuando pelo processador", _filePath);
                    _queue.Return(frame);
                    decoder.Dispose();
                    decoder = MediaDecoder.Open(_filePath, _onAudioSamples, allowHardware: true);
                    if (lastPts > 0)
                    {
                        decoder.Seek(lastPts);
                        DiscardUntil(decoder, lastPts, ct);
                    }
                    continue;
                }
                if (decoded) lastPts = frame.PtsSeconds;

                if (!decoded)
                {
                    // EOF: quem manda no transporte é o ClipPlayer (Loop = ele pede o
                    // seek; Hold/Once = ninguém pede). Loopar aqui sozinho fazia o clip
                    // em Hold "re-rodar" na velocidade do render (~20x) e atropelava o
                    // áudio — o worker agora só espera o próximo seek.
                    _queue.Return(frame);
                    while (!ct.IsCancellationRequested && !_seekPending)
                        Thread.Sleep(5);
                    continue;
                }

                // Atualizar estatísticas de fps.
                Interlocked.Increment(ref _decodedFrames);
                if (_statsClock.ElapsedMilliseconds >= 1000)
                {
                    _decodeFps = _decodedFrames / _statsClock.Elapsed.TotalSeconds;
                    _statsClock.Restart();
                    Interlocked.Exchange(ref _decodedFrames, 0);
                }

                // Colocar na fila (bloqueia se cheia — back-pressure natural).
                _queue.TryOffer(frame, gen, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Encerramento normal.
        }
        catch (MediaOpenException ex)
        {
            Log.Error(ex, "Erro no decode worker: {File}", _filePath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Erro inesperado no decode worker: {File}", _filePath);
        }
        finally
        {
            decoder?.Dispose();
            Log.Debug("Decode worker encerrado: {File}", _filePath);
        }
    }

    private static void DiscardUntil(MediaDecoder decoder, double targetPts, CancellationToken ct)
    {
        // Decodifica e descarta até chegar num frame com PTS próximo do alvo.
        var temp = new DecodedFrame();
        for (int i = 0; i < 120 && !ct.IsCancellationRequested; i++)
        {
            if (!decoder.TryDecodeNext(temp)) break;
            if (temp.PtsSeconds >= targetPts - 0.01) break;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _queue.Clear(); // unblock produtor se preso
        _thread.Join(TimeSpan.FromSeconds(3));
        _cts.Dispose();
    }
}
