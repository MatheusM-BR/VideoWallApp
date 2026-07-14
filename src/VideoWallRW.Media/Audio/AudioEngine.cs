using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Serilog;

namespace VideoWallRW.Media.Audio;

/// <summary>
/// Host único do dispositivo de áudio (WASAPI shared mode) com um mixer de tracks
/// por clip. Criado e destruído sempre na mesma thread dedicada — requisito do WASAPI.
/// Falha ao abrir o dispositivo de áudio NUNCA derruba o programa: <see cref="Create"/>
/// retorna null e os clipes seguem tocando normalmente, apenas sem som.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private readonly MixingSampleProvider _mixer;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly ManualResetEventSlim _stop = new(false);
    private WasapiOut? _output;
    private Exception? _startupError;

    private AudioEngine()
    {
        _mixer = new MixingSampleProvider(ClipAudioTrack.Format) { ReadFully = true };
        _thread = new Thread(ThreadLoop) { Name = "AudioEngine", IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    /// <summary>Cria e inicializa o dispositivo padrão. Retorna null em caso de falha (log já emitido).</summary>
    public static AudioEngine? Create()
    {
        var engine = new AudioEngine();
        engine._thread.Start();
        if (!engine._ready.Wait(TimeSpan.FromSeconds(5)))
        {
            Log.Warning("Timeout ao inicializar o AudioEngine — clipes tocarão sem som.");
            return null;
        }
        if (engine._startupError != null)
        {
            Log.Warning(engine._startupError, "Falha ao inicializar saída de áudio — clipes tocarão sem som.");
            return null;
        }
        return engine;
    }

    public void AddTrack(ClipAudioTrack track) => _mixer.AddMixerInput(track);
    public void RemoveTrack(ClipAudioTrack track) => _mixer.RemoveMixerInput(track);

    private volatile bool _masterMute;

    /// <summary>Mute geral: zera a saída inteira do mixer sem mexer nos volumes por
    /// clip — desmutar volta exatamente ao estado anterior. Thread-safe.</summary>
    public bool MasterMute
    {
        get => _masterMute;
        set => _masterMute = value;
    }

    /// <summary>Gate de mute entre o mixer e o WASAPI: continua CONSUMINDO os samples
    /// (os tracks seguem avançando em sincronia com o vídeo), só silencia a saída.</summary>
    private sealed class MuteGate(NAudio.Wave.ISampleProvider inner, Func<bool> muted) : NAudio.Wave.ISampleProvider
    {
        public WaveFormat WaveFormat => inner.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, count);
            if (muted())
                Array.Clear(buffer, offset, read);
            return read;
        }
    }

    private void ThreadLoop()
    {
        try
        {
            _output = new WasapiOut(AudioClientShareMode.Shared, 100);
            _output.Init(new SampleToWaveProvider(new MuteGate(_mixer, () => _masterMute)));
            _output.Play();
            Log.Information("AudioEngine iniciado (WASAPI shared, 100ms)");
        }
        catch (Exception ex)
        {
            _startupError = ex;
            _ready.Set();
            return;
        }
        _ready.Set();

        _stop.Wait();

        try { _output.Stop(); }
        catch (Exception ex) { Log.Debug(ex, "Erro ao parar WasapiOut (ignorado no shutdown)"); }
        _output.Dispose();
    }

    public void Dispose()
    {
        _stop.Set();
        _thread.Join(TimeSpan.FromSeconds(3));
        _ready.Dispose();
        _stop.Dispose();
        Log.Information("AudioEngine encerrado");
    }
}
