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
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private int _generation; // Interlocked
    private volatile bool _seekPending;
    private double _seekTarget;
    private bool _loop = true;

    // Estatísticas expostas para o snapshot da UI.
    private long _decodedFrames;
    private readonly Stopwatch _statsClock = Stopwatch.StartNew();
    private double _decodeFps;

    public double DecodeFps => _decodeFps;

    public VideoDecodeWorker(string filePath, FrameQueue queue)
    {
        _filePath = filePath;
        _queue = queue;
        _thread = new Thread(ThreadLoop) { Name = $"Decode:{Path.GetFileName(filePath)}", IsBackground = true };
        _thread.Start();
    }

    /// <summary>Solicita seek. Seguro chamar da render thread.</summary>
    public void RequestSeek(double seconds)
    {
        _seekTarget = seconds;
        Interlocked.Increment(ref _generation);
        _queue.Clear();
        _seekPending = true;
    }

    private void ThreadLoop()
    {
        var ct = _cts.Token;
        MediaDecoder? decoder = null;
        try
        {
            decoder = MediaDecoder.Open(_filePath);
            Log.Information("Decode worker iniciado: {File}", _filePath);

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

                if (!decoder.TryDecodeNext(frame))
                {
                    // EOF.
                    _queue.Return(frame);
                    if (_loop)
                    {
                        // Loop sem frame preto: seek + continuar.
                        decoder.Seek(0);
                        Interlocked.Increment(ref _generation);
                        _queue.Clear();
                        continue;
                    }
                    break; // modo não-loop: sair da thread
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
