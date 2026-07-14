using FFmpeg.AutoGen;
using Serilog;
using VideoWallRW.Media.Decoding;

namespace VideoWallRW.Media.Hap;

/// <summary>
/// Demuxa um arquivo .mov com HAP sem passar pelo codec do FFmpeg — o decoder HAP
/// do FFmpeg descomprime DXT→RGBA na CPU e joga fora a vantagem do formato (ver
/// docs/fases/FASE-6.md T6.4). Aqui só usamos o FFmpeg para abrir o contêiner e
/// entregar os pacotes brutos; <see cref="HapSection.Decode"/> extrai os bytes DXT.
/// </summary>
public sealed unsafe class HapDemuxer : IDisposable
{
    private AVFormatContext* _fmtCtx;
    private AVPacket* _packet;
    private readonly int _videoStreamIndex;
    private bool _disposed;

    public double Duration { get; }
    public int Width { get; }
    public int Height { get; }
    public double FrameRate { get; }
    public uint CodecTag { get; }

    private readonly AVRational _timeBase;
    private double _lastPts = -1;

    private HapDemuxer(AVFormatContext* fmtCtx, int streamIndex, AVRational timeBase,
        double duration, int width, int height, double frameRate, uint codecTag)
    {
        _fmtCtx = fmtCtx;
        _videoStreamIndex = streamIndex;
        _timeBase = timeBase;
        Duration = duration;
        Width = width;
        Height = height;
        FrameRate = frameRate;
        CodecTag = codecTag;
        _packet = ffmpeg.av_packet_alloc();
    }

    /// <summary>
    /// Probe barato: abre só o cabeçalho para ler o fourcc do codec e fecha —
    /// usado pela fábrica de clipes para decidir entre <c>HapVideoSource</c> (Hap1/
    /// Hap5) e o caminho normal de vídeo (FFmpeg). Nunca lança — arquivo inválido
    /// ou sem stream de vídeo simplesmente retorna null.
    /// </summary>
    public static string? TryDetectFourCc(string path)
    {
        try
        {
            using var demuxer = Open(path);
            var bytes = BitConverter.GetBytes(demuxer.CodecTag);
            return new string([(char)bytes[0], (char)bytes[1], (char)bytes[2], (char)bytes[3]]);
        }
        catch
        {
            return null;
        }
    }

    public static HapDemuxer Open(string path)
    {
        AVFormatContext* fmtCtx = null;
        var ret = ffmpeg.avformat_open_input(&fmtCtx, path, null, null);
        if (ret < 0)
            throw new MediaOpenException($"Não foi possível abrir '{path}': {AvError(ret)}", ret);

        ret = ffmpeg.avformat_find_stream_info(fmtCtx, null);
        if (ret < 0)
        {
            ffmpeg.avformat_close_input(&fmtCtx);
            throw new MediaOpenException($"Não foi possível ler informações do stream: {AvError(ret)}", ret);
        }

        int streamIndex = ffmpeg.av_find_best_stream(fmtCtx, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
        if (streamIndex < 0)
        {
            ffmpeg.avformat_close_input(&fmtCtx);
            throw new MediaOpenException($"Nenhuma stream de vídeo encontrada em '{path}'.");
        }

        var stream = fmtCtx->streams[streamIndex];
        var codecpar = stream->codecpar;
        var timeBase = stream->time_base;
        double duration = fmtCtx->duration <= 0 ? 0 : fmtCtx->duration / (double)ffmpeg.AV_TIME_BASE;
        double frameRate = stream->avg_frame_rate.den == 0 ? 30 : ffmpeg.av_q2d(stream->avg_frame_rate);

        Log.Debug("HAP demuxer aberto: {Path} — {W}×{H} @ {Fps:F2}fps, duração {Dur:F2}s",
            path, codecpar->width, codecpar->height, frameRate, duration);

        return new HapDemuxer(fmtCtx, streamIndex, timeBase, duration, codecpar->width, codecpar->height,
            frameRate, codecpar->codec_tag);
    }

    /// <summary>Lê o próximo pacote de vídeo bruto. Retorna false no EOF.</summary>
    public bool TryReadNext(HapPacket target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (true)
        {
            var ret = ffmpeg.av_read_frame(_fmtCtx, _packet);
            if (ret == ffmpeg.AVERROR_EOF)
                return false;
            if (ret < 0)
                throw new MediaOpenException($"av_read_frame: {AvError(ret)}", ret);

            if (_packet->stream_index != _videoStreamIndex)
            {
                ffmpeg.av_packet_unref(_packet);
                continue;
            }

            long rawPts = _packet->pts == ffmpeg.AV_NOPTS_VALUE ? _packet->dts : _packet->pts;
            double pts = rawPts == ffmpeg.AV_NOPTS_VALUE
                ? (_lastPts < 0 ? 0 : _lastPts + 1.0 / FrameRate)
                : rawPts * ffmpeg.av_q2d(_timeBase);
            _lastPts = pts;

            if (target.Data.Length < _packet->size)
                target.Data = new byte[_packet->size];
            var span = new ReadOnlySpan<byte>(_packet->data, _packet->size);
            span.CopyTo(target.Data);
            target.Length = _packet->size;
            target.PtsSeconds = pts;

            ffmpeg.av_packet_unref(_packet);
            return true;
        }
    }

    public void Seek(double seconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long ts = (long)(seconds / ffmpeg.av_q2d(_timeBase));
        ffmpeg.av_seek_frame(_fmtCtx, _videoStreamIndex, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);
        _lastPts = -1;
    }

    private static string AvError(int code)
    {
        const int bufSize = 256;
        var buf = stackalloc byte[bufSize];
        ffmpeg.av_strerror(code, buf, (ulong)bufSize);
        return System.Runtime.InteropServices.Marshal.PtrToStringAnsi((nint)buf) ?? code.ToString();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_packet != null) { fixed (AVPacket** p = &_packet) ffmpeg.av_packet_free(p); }
        if (_fmtCtx != null) { fixed (AVFormatContext** p = &_fmtCtx) ffmpeg.avformat_close_input(p); }
    }
}

/// <summary>Buffer reutilizável para um pacote HAP bruto (ainda comprimido).</summary>
public sealed class HapPacket
{
    public byte[] Data { get; set; } = [];
    public int Length { get; set; }
    public double PtsSeconds { get; set; }
    internal int Generation { get; set; }

    public ReadOnlySpan<byte> Span => Data.AsSpan(0, Length);
}
