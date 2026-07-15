using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Serilog;
using VideoWallRW.Engine;
using VideoWallRW.Engine.Graphics;
using VideoWallRW.Media;
using VideoWallRW.Media.Audio;
using VideoWallRW.Media.Hap;
using VideoWallRW.Media.Sources;
using VideoWallRW.Core.Persistence;

namespace VideoWallRW.App;

public partial class App : Application
{
    private GraphicsDevice? _gfx;
    private RenderThread? _render;
    private AudioEngine? _audio;
    private HeartbeatClient? _heartbeat;
    private Mutex? _singleInstanceMutex;
    private Input.OscService? _osc;
    private Remote.RemoteWebService? _remoteWeb;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        // O processo também cria seu próprio device D3D11 (RenderThread) além do que o WPF
        // usa internamente para desenhar suas próprias janelas — em algumas combinações de
        // GPU/driver isso deixa a janela do painel em branco até o primeiro repaint forçado.
        // O painel não tem nada pesado para desenhar (o vídeo vai pelas janelas de saída,
        // não pelo WPF), então cair para software aqui não tem custo perceptível.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        // Instância única: duas cópias do programa brigam pelas saídas e tocam áudio
        // em dobro (som "picotado" e mute que parece não funcionar — cada instância
        // tem sua própria sessão WASAPI).
        _singleInstanceMutex = new Mutex(true, @"Global\VideoWallRW-SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "O VideoWallRW já está aberto. Use a janela existente —\n" +
                "duas cópias ao mesmo tempo brigam pelas saídas e pelo áudio.",
                "VideoWallRW", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(0);
            return;
        }

        // T7.3: sentinela de sessão — se ainda existir no próximo start, o
        // encerramento anterior não foi limpo (crash), e a UI oferece recuperação.
        AutosaveService.MarkSessionStart();

        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VideoWallRW", "logs");
        Directory.CreateDirectory(logDir);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(Path.Combine(logDir, "app-.log"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
            .WriteTo.Debug()
            .CreateLogger();
        Log.Information("VideoWallRW iniciando");

        // Inicializar FFmpeg — DLLs ficam na mesma pasta do exe (copiadas pelo csproj).
        try
        {
            FFmpegBootstrap.Initialize(AppContext.BaseDirectory);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Falha ao inicializar o FFmpeg");
            MessageBox.Show(
                $"Não foi possível inicializar o FFmpeg.\n" +
                $"Verifique se as DLLs estão em:\n{AppContext.BaseDirectory}\n\nErro: {ex.Message}",
                "VideoWallRW", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        try
        {
            _gfx = GraphicsDevice.Create();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Falha ao criar o device D3D11");
            MessageBox.Show($"Falha ao inicializar o Direct3D 11:\n{ex.Message}",
                "VideoWallRW", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // Falha ao abrir o dispositivo de áudio (sem placa de som, RDP sem áudio redirecionado
        // etc.) nunca derruba o programa — os clipes seguem tocando normalmente, sem som.
        _audio = AudioEngine.Create();

        _render = new RenderThread(_gfx);

        // Factory injetada: cria VideoFileSource no contexto do App (que tem referência a Media).
        // Isso mantém Engine sem dependência direta de Media.
        var gfxRef = _gfx;
        var audioRef = _audio;
        _render.SetClipFactory((clip, defaultImageDuration) =>
        {
            return clip.Source switch
            {
                Core.Model.FileVideoSource v => CreateVideoSource(gfxRef, audioRef, v.Path),
                Core.Model.FileImageSource i => new ImageFileSource(gfxRef, i.Path)
                {
                    Duration = clip.ImageDuration ?? defaultImageDuration
                },
                _ => throw new NotSupportedException($"Tipo de fonte {clip.Source.GetType()} não é suportada por esta factory.")
            };
        });

        _render.Start();
        _heartbeat = new HeartbeatClient();

        bool kiosk = e.Args.Any(a => a.Equals("--kiosk", StringComparison.OrdinalIgnoreCase));
        var vm = new MainViewModel(_gfx, _render, kiosk);
        if (kiosk)
            ApplyKiosk(vm);

        _osc = new Input.OscService(_render, vm.ResolveSceneByName);
        _osc.Start();

        _remoteWeb = new Remote.RemoteWebService(_render, vm.BuildRemoteSnapshot);
        _ = _remoteWeb.StartAsync();

        var window = new MainWindow(vm);
        MainWindow = window;
        if (kiosk)
            window.WindowState = WindowState.Maximized;
        window.Show();
    }

    /// <summary>
    /// T8.1: modo instalação — abre sem diálogo de recuperação (recupera sozinho) e
    /// retoma o último projeto + estado de runtime salvo, para reiniciar sem operador
    /// presente (watchdog reinicia o processo, kiosk retoma o que estava no ar).
    /// </summary>
    private static void ApplyKiosk(MainViewModel vm)
    {
        try
        {
            var runtime = RuntimeStateService.TryLoad();
            if (runtime?.ProjectPath is { } path && File.Exists(path))
            {
                vm.LoadProjectSilently(path);
                vm.ApplyRuntimeState(runtime);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Falha ao retomar estado em modo kiosk");
        }
    }

    /// <summary>
    /// HAP é transparente pro operador: mesmo tipo de clip (FileVideoSource), a
    /// fábrica decide o caminho de decode olhando o fourcc do codec. Hap1/Hap5 vão
    /// pelo caminho DXT direto na GPU (Fase 6); qualquer outra coisa (incluindo
    /// HapQ, ainda não suportado) cai no decode normal por FFmpeg.
    /// </summary>
    private static IPlayableSource CreateVideoSource(GraphicsDevice gfx, AudioEngine? audio, string path)
    {
        string? fourCc = Path.GetExtension(path).Equals(".mov", StringComparison.OrdinalIgnoreCase)
            ? HapDemuxer.TryDetectFourCc(path)
            : null;

        if (fourCc is "Hap1" or "Hap5")
        {
            try
            {
                return new HapVideoSource(gfx, path);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Falha ao abrir '{Path}' como HAP — caindo para decode normal.", path);
            }
        }
        return new VideoFileSource(gfx, path, audio);
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        // Ordem importa: parar a thread (que descarta as saídas) antes do device;
        // o áudio pode ser encerrado a qualquer momento, é independente do D3D11.
        _heartbeat?.Dispose();
        _osc?.Dispose();
        try { _remoteWeb?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); } catch { /* melhor esforço no fechamento */ }
        _render?.Stop();
        _audio?.Dispose();
        _gfx?.Dispose();
        AutosaveService.MarkSessionClean();
        Log.Information("VideoWallRW encerrado");
        Log.CloseAndFlush();
    }
}
