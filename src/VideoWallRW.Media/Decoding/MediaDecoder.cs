using FFmpeg.AutoGen;
using Serilog;

namespace VideoWallRW.Media.Decoding;

/// <summary>
/// Wrapper sobre FFmpeg: abre, demultiplexa e decodifica vídeo.
/// Nenhum tipo de FFmpeg.AutoGen vaza fora desta classe.
/// Deve ser acessado apenas pela decode thread.
/// </summary>
public sealed unsafe class MediaDecoder : IDisposable
{
    /// <summary>Formato canônico de saída de áudio: estéreo, 48kHz, float32 interleaved.</summary>
    public const int AudioOutChannels = 2;
    public const int AudioOutSampleRate = 48000;

    private AVFormatContext* _fmtCtx;
    private AVCodecContext* _codecCtx;
    private SwsContext* _swsCtx;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private int _videoStreamIndex;
    private bool _disposed;
    private double _lastPts = -1;

    private AVCodecContext* _audioCodecCtx;
    private SwrContext* _swrCtx;
    private AVFrame* _audioFrame;
    private int _audioStreamIndex = -1;
    private byte[] _audioScratch = [];
    private readonly Action<float[], int>? _onAudioSamples;

    public double Duration { get; }
    public int Width { get; }
    public int Height { get; }
    public double FrameRate { get; }
    public bool HasAudio => _audioStreamIndex >= 0;

    /// <summary>Format-ratios do stream (para cálculo de PTS).</summary>
    private readonly AVRational _timeBase;

    private MediaDecoder(AVFormatContext* fmtCtx, AVCodecContext* codecCtx,
        int streamIndex, AVRational timeBase, double duration,
        int width, int height, double frameRate,
        AVCodecContext* audioCodecCtx, int audioStreamIndex, SwrContext* swrCtx,
        Action<float[], int>? onAudioSamples)
    {
        _fmtCtx = fmtCtx;
        _codecCtx = codecCtx;
        _videoStreamIndex = streamIndex;
        _timeBase = timeBase;
        Duration = duration;
        Width = width;
        Height = height;
        FrameRate = frameRate;
        _audioCodecCtx = audioCodecCtx;
        _audioStreamIndex = audioStreamIndex;
        _swrCtx = swrCtx;
        _onAudioSamples = onAudioSamples;

        _packet = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();
        if (_audioStreamIndex >= 0)
            _audioFrame = ffmpeg.av_frame_alloc();
    }

    /// <summary>
    /// Abre o arquivo e retorna um <see cref="MediaDecoder"/> pronto.
    /// Lança <see cref="MediaOpenException"/> com mensagem legível em caso de falha.
    /// <paramref name="onAudioSamples"/>, se informado, é chamado (na thread que decodifica)
    /// com amostras float32 estéreo 48kHz interleaved sempre que houver áudio novo.
    /// Falha ao abrir/inicializar áudio nunca impede a abertura do vídeo — o clipe
    /// simplesmente segue sem som.
    /// </summary>
    public static MediaDecoder Open(string path, Action<float[], int>? onAudioSamples = null)
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

        AVCodec* codec = null;
        int streamIndex = ffmpeg.av_find_best_stream(fmtCtx, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0);
        if (streamIndex < 0)
        {
            ffmpeg.avformat_close_input(&fmtCtx);
            throw new MediaOpenException($"Nenhuma stream de vídeo encontrada em '{path}'.");
        }

        var codecCtx = ffmpeg.avcodec_alloc_context3(codec);
        if (codecCtx == null)
        {
            ffmpeg.avformat_close_input(&fmtCtx);
            throw new MediaOpenException("Falha ao alocar contexto de codec.");
        }

        ret = ffmpeg.avcodec_parameters_to_context(codecCtx, fmtCtx->streams[streamIndex]->codecpar);
        if (ret < 0)
        {
            ffmpeg.avcodec_free_context(&codecCtx);
            ffmpeg.avformat_close_input(&fmtCtx);
            throw new MediaOpenException($"Falha ao copiar parâmetros do codec: {AvError(ret)}", ret);
        }

        // Multi-threaded software decode.
        codecCtx->thread_count = 0; // auto (deixar o FFmpeg decidir)

        ret = ffmpeg.avcodec_open2(codecCtx, codec, null);
        if (ret < 0)
        {
            ffmpeg.avcodec_free_context(&codecCtx);
            ffmpeg.avformat_close_input(&fmtCtx);
            throw new MediaOpenException($"Falha ao abrir codec '{ffmpeg.avcodec_get_name(codecCtx->codec_id)}': {AvError(ret)}", ret);
        }

        var stream = fmtCtx->streams[streamIndex];
        var timeBase = stream->time_base;
        double duration = fmtCtx->duration <= 0 ? 0
            : fmtCtx->duration / (double)ffmpeg.AV_TIME_BASE;
        double frameRate = stream->avg_frame_rate.den == 0 ? 30
            : ffmpeg.av_q2d(stream->avg_frame_rate);
        int width = codecCtx->width;
        int height = codecCtx->height;

        Log.Debug("Vídeo aberto: {Path} — {W}×{H} @ {Fps:F2}fps, duração {Dur:F2}s, codec {Codec}",
            path, width, height, frameRate, duration,
            ffmpeg.avcodec_get_name(codecCtx->codec_id));

        OpenAudio(path, fmtCtx, out var audioCodecCtx, out var audioStreamIndex, out var swrCtx);

        return new MediaDecoder(fmtCtx, codecCtx, streamIndex, timeBase, duration, width, height, frameRate,
            audioCodecCtx, audioStreamIndex, swrCtx, onAudioSamples);
    }

    /// <summary>
    /// Tenta abrir a melhor stream de áudio e preparar o reamostrador para o formato
    /// canônico (estéreo/48kHz/float32). Nunca lança — falha aqui apenas desativa o
    /// áudio deste clip, o vídeo continua normalmente. (Tupla com ponteiros não é
    /// permitida em C# — por isso os resultados saem via <c>out</c>.)
    /// </summary>
    private static void OpenAudio(string path, AVFormatContext* fmtCtx,
        out AVCodecContext* audioCodecCtx, out int audioStreamIndex, out SwrContext* swr)
    {
        audioCodecCtx = null;
        audioStreamIndex = -1;
        swr = null;

        AVCodec* audioCodec = null;
        int streamIndex = ffmpeg.av_find_best_stream(
            fmtCtx, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, &audioCodec, 0);
        if (streamIndex < 0)
            return;

        var codecCtx = ffmpeg.avcodec_alloc_context3(audioCodec);
        if (codecCtx == null)
        {
            Log.Warning("Falha ao alocar contexto de áudio em '{Path}' — clipe seguirá sem som.", path);
            return;
        }

        var ret = ffmpeg.avcodec_parameters_to_context(codecCtx, fmtCtx->streams[streamIndex]->codecpar);
        if (ret >= 0)
        {
            codecCtx->thread_count = 0;
            ret = ffmpeg.avcodec_open2(codecCtx, audioCodec, null);
        }
        if (ret < 0)
        {
            Log.Warning("Falha ao abrir codec de áudio em '{Path}': {Err} — clipe seguirá sem som.",
                path, AvError(ret));
            ffmpeg.avcodec_free_context(&codecCtx);
            return;
        }

        AVChannelLayout outLayout = default;
        ffmpeg.av_channel_layout_default(&outLayout, AudioOutChannels);
        SwrContext* swrCtx = null;
        var swrRet = ffmpeg.swr_alloc_set_opts2(&swrCtx, &outLayout, AVSampleFormat.AV_SAMPLE_FMT_FLT, AudioOutSampleRate,
            &codecCtx->ch_layout, codecCtx->sample_fmt, codecCtx->sample_rate, 0, null);
        ffmpeg.av_channel_layout_uninit(&outLayout);

        if (swrRet < 0 || swrCtx == null || ffmpeg.swr_init(swrCtx) < 0)
        {
            Log.Warning("Falha ao inicializar reamostrador de áudio em '{Path}' — clipe seguirá sem som.", path);
            if (swrCtx != null) ffmpeg.swr_free(&swrCtx);
            ffmpeg.avcodec_free_context(&codecCtx);
            return;
        }

        Log.Debug("Áudio aberto: {Path} — {Ch}ch @ {Rate}Hz, codec {Codec}",
            path, codecCtx->ch_layout.nb_channels, codecCtx->sample_rate,
            ffmpeg.avcodec_get_name(codecCtx->codec_id));

        audioCodecCtx = codecCtx;
        audioStreamIndex = streamIndex;
        swr = swrCtx;
    }

    /// <summary>
    /// Decodifica o próximo frame e preenche <paramref name="target"/>.
    /// Retorna false no EOF. Thread-safe apenas na decode thread.
    /// </summary>
    public bool TryDecodeNext(DecodedFrame target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        while (true)
        {
            // Tentar receber frame do codecCtx antes de ler mais pacotes.
            var ret = ffmpeg.avcodec_receive_frame(_codecCtx, _frame);
            if (ret == 0)
            {
                FillTarget(target);
                return true;
            }
            if (ret == ffmpeg.AVERROR_EOF)
                return false; // codec drenado após envio de flush packet
            if (ret != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                throw new MediaOpenException($"avcodec_receive_frame: {AvError(ret)}", ret);

            // Precisamos de mais dados: ler pacote.
            ret = ffmpeg.av_read_frame(_fmtCtx, _packet);
            if (ret == ffmpeg.AVERROR_EOF)
            {
                // Sinalizar EOF ao codec para drenar frames restantes.
                ffmpeg.avcodec_send_packet(_codecCtx, null);
                // Não retornamos false ainda — o loop vai receber frames pendentes.
                continue;
            }
            if (ret < 0)
                throw new MediaOpenException($"av_read_frame: {AvError(ret)}", ret);

            if (_packet->stream_index == _videoStreamIndex)
            {
                var sendRet = ffmpeg.avcodec_send_packet(_codecCtx, _packet);
                if (sendRet < 0 && sendRet != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    Log.Warning("avcodec_send_packet retornou {Code}", AvError(sendRet));
            }
            else if (_packet->stream_index == _audioStreamIndex)
            {
                DecodeAudioPacket();
            }
            ffmpeg.av_packet_unref(_packet);
        }
    }

    /// <summary>
    /// Seek para aproximadamente <paramref name="seconds"/> segundos.
    /// Deve ser seguido por descarte de frames até PTS ≥ target (feito pelo VideoDecodeWorker).
    /// </summary>
    public void Seek(double seconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long ts = (long)(seconds / ffmpeg.av_q2d(_timeBase));
        // AVSEEK_FLAG_BACKWARD: ir ao keyframe anterior ao alvo (necessário para H.264).
        ffmpeg.av_seek_frame(_fmtCtx, _videoStreamIndex, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);
        ffmpeg.avcodec_flush_buffers(_codecCtx);
        if (_audioCodecCtx != null)
            ffmpeg.avcodec_flush_buffers(_audioCodecCtx);
        _lastPts = -1;
    }

    /// <summary>Decodifica um pacote de áudio e reamostra cada frame resultante.</summary>
    private void DecodeAudioPacket()
    {
        var sendRet = ffmpeg.avcodec_send_packet(_audioCodecCtx, _packet);
        if (sendRet < 0 && sendRet != ffmpeg.AVERROR(ffmpeg.EAGAIN))
        {
            Log.Warning("avcodec_send_packet (áudio) retornou {Code}", AvError(sendRet));
            return;
        }

        while (true)
        {
            var ret = ffmpeg.avcodec_receive_frame(_audioCodecCtx, _audioFrame);
            if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                break;
            if (ret < 0)
            {
                Log.Warning("avcodec_receive_frame (áudio) retornou {Code}", AvError(ret));
                break;
            }
            ResampleAndEmit();
            ffmpeg.av_frame_unref(_audioFrame);
        }
    }

    private void ResampleAndEmit()
    {
        int maxOutSamples = ffmpeg.swr_get_out_samples(_swrCtx, _audioFrame->nb_samples);
        if (maxOutSamples <= 0)
            return;

        int neededBytes = maxOutSamples * AudioOutChannels * sizeof(float);
        if (_audioScratch.Length < neededBytes)
            _audioScratch = new byte[neededBytes];

        int converted;
        byte*[] inPlanes = _audioFrame->data;
        fixed (byte** inPtr = inPlanes)
        fixed (byte* outBufPtr = _audioScratch)
        {
            byte*[] outPlanes = [outBufPtr];
            fixed (byte** outPtr = outPlanes)
            {
                converted = ffmpeg.swr_convert(_swrCtx, outPtr, maxOutSamples, inPtr, _audioFrame->nb_samples);
            }
        }
        if (converted <= 0)
            return;

        int floatCount = converted * AudioOutChannels;
        var samples = new float[floatCount];
        Buffer.BlockCopy(_audioScratch, 0, samples, 0, floatCount * sizeof(float));
        _onAudioSamples?.Invoke(samples, floatCount);
    }

    private void FillTarget(DecodedFrame target)
    {
        // PTS em segundos.
        long rawPts = _frame->best_effort_timestamp;
        double pts = rawPts == ffmpeg.AV_NOPTS_VALUE
            ? (_lastPts < 0 ? 0 : _lastPts + (1.0 / FrameRate))
            : rawPts * ffmpeg.av_q2d(_timeBase);
        _lastPts = pts;

        int dstStride = Width * 4; // BGRA: 4 bytes por pixel, sem padding extra
        int needed = dstStride * Height;
        if (target.Pixels.Length < needed)
            target.Pixels = new byte[needed];

        // Contexto sws reutilizado: recriar só se resolução/formato mudar.
        if (_swsCtx == null ||
            _frame->width != Width || _frame->height != Height)
        {
            if (_swsCtx != null) ffmpeg.sws_freeContext(_swsCtx);
            _swsCtx = ffmpeg.sws_getContext(
                _frame->width, _frame->height, (AVPixelFormat)_frame->format,
                Width, Height, AVPixelFormat.AV_PIX_FMT_BGRA,
                ffmpeg.SWS_BILINEAR, null, null, null);
            if (_swsCtx == null)
                throw new MediaOpenException("sws_getContext falhou.");
        }

        fixed (byte* dstPtr = target.Pixels)
        {
            byte*[] dstData = [dstPtr];
            int[] dstLinesize = [dstStride];
            ffmpeg.sws_scale(_swsCtx, _frame->data, _frame->linesize, 0, _frame->height,
                dstData, dstLinesize);

            // sws_scale não garante alpha=255 ao converter de um formato sem alpha (ex.: YUV420P)
            // para BGRA — em vários builds do FFmpeg o byte de alpha sai zerado. Vídeo é sempre
            // opaco no escopo atual (sem transparência real), e o pixel shader faz o premultiply
            // por esse canal (col.rgb *= col.a) — alpha=0 apagaria a imagem inteira mesmo com os
            // pixels RGB corretos. Forçar opaco aqui é o ponto certo: um único lugar, sem
            // depender do comportamento de uma versão específica do swscale.
            byte* p = dstPtr;
            for (int i = 3; i < needed; i += 4)
                p[i] = 0xFF;
        }

        target.Stride = dstStride;
        target.Width = Width;
        target.Height = Height;
        target.PtsSeconds = pts;
        ffmpeg.av_frame_unref(_frame);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
        DisposeCore();
    }

    ~MediaDecoder()
    {
        // Finalizer indica uso incorreto (não foi chamado Dispose).
        Log.Warning("MediaDecoder finalizado sem Dispose — possível vazamento de recursos FFmpeg.");
        DisposeCore();
    }

    private void DisposeCore()
    {
        if (_swsCtx != null) { ffmpeg.sws_freeContext(_swsCtx); _swsCtx = null; }
        if (_swrCtx != null) { fixed (SwrContext** p = &_swrCtx) ffmpeg.swr_free(p); }
        if (_frame != null) { fixed (AVFrame** p = &_frame) ffmpeg.av_frame_free(p); }
        if (_audioFrame != null) { fixed (AVFrame** p = &_audioFrame) ffmpeg.av_frame_free(p); }
        if (_packet != null) { fixed (AVPacket** p = &_packet) ffmpeg.av_packet_free(p); }
        if (_codecCtx != null) { fixed (AVCodecContext** p = &_codecCtx) ffmpeg.avcodec_free_context(p); }
        if (_audioCodecCtx != null) { fixed (AVCodecContext** p = &_audioCodecCtx) ffmpeg.avcodec_free_context(p); }
        if (_fmtCtx != null) { fixed (AVFormatContext** p = &_fmtCtx) ffmpeg.avformat_close_input(p); }
    }

    private static string AvError(int code)
    {
        const int bufSize = 256;
        var buf = stackalloc byte[bufSize];
        ffmpeg.av_strerror(code, buf, (ulong)bufSize);
        return System.Runtime.InteropServices.Marshal.PtrToStringAnsi((nint)buf) ?? code.ToString();
    }
}
