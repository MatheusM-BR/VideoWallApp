using Serilog;

namespace VideoWallRW.Media.Hap;

/// <summary>Thread de decodificação de um clip HAP — demuxa + descomprime (Snappy/None), sem codec.</summary>
public sealed class HapDecodeWorker : IDisposable
{
    private readonly string _filePath;
    private readonly HapFrameQueue _queue;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private int _generation;
    private volatile bool _seekPending;
    private double _seekTarget;

    public HapDecodeWorker(string filePath, HapFrameQueue queue)
    {
        _filePath = filePath;
        _queue = queue;
        _thread = new Thread(ThreadLoop) { Name = $"HapDecode:{Path.GetFileName(filePath)}", IsBackground = true };
        _thread.Start();
    }

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
        HapDemuxer? demuxer = null;
        var packet = new HapPacket();
        try
        {
            demuxer = HapDemuxer.Open(_filePath);
            Log.Information("HAP decode worker iniciado: {File}", _filePath);

            while (!ct.IsCancellationRequested)
            {
                if (_seekPending)
                {
                    _seekPending = false;
                    demuxer.Seek(_seekTarget);
                    Interlocked.Increment(ref _generation);
                    _queue.Clear();
                }

                int gen = Volatile.Read(ref _generation);

                if (!demuxer.TryReadNext(packet))
                {
                    // EOF: loop sem frame preto — volta ao início e continua.
                    demuxer.Seek(0);
                    Interlocked.Increment(ref _generation);
                    _queue.Clear();
                    continue;
                }

                HapTextureFormat format;
                byte[] dxtBytes;
                try
                {
                    (format, dxtBytes) = HapSection.Decode(packet.Span);
                }
                catch (HapDecodeException ex)
                {
                    Log.Error(ex, "Frame HAP inválido em {File} — pulando frame", _filePath);
                    continue;
                }

                var frame = _queue.Rent();
                frame.Generation = gen;
                frame.Format = format;
                frame.DxtBytes = dxtBytes;
                frame.Width = demuxer.Width;
                frame.Height = demuxer.Height;
                frame.PtsSeconds = packet.PtsSeconds;

                _queue.TryOffer(frame, gen, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Encerramento normal.
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Erro no HAP decode worker: {File}", _filePath);
        }
        finally
        {
            demuxer?.Dispose();
            Log.Debug("HAP decode worker encerrado: {File}", _filePath);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _queue.Clear();
        _thread.Join(TimeSpan.FromSeconds(3));
        _cts.Dispose();
    }
}
