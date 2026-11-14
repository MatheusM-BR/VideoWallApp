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
    private bool _sessionStarted;
    private Input.OscService? _osc;
    private Remote.RemoteWebService? _remoteWeb;
    private Media.Ndi.NdiFinder? _ndiFinder;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        // Verification mode is also used by the installer; never starts audio,
        // rendering, network services or touches the user's recovery state.
        bool verifyOnly = e.Args.Contains("--verify-package", StringComparer.OrdinalIgnoreCase);
        string? integrityError = Security.PackageIntegrity.VerifyCurrentPackage();
        if (integrityError is not null || verifyOnly)
        {
            if (integrityError is not null && !verifyOnly)
                MessageBox.Show(integrityError, "RW VideoWall — integridade", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(integrityError is null ? 0 : 10);
            return;
        }

        // Rede de segurança de produção: um erro inesperado na UI thread (ex.: uma
        // sonda assíncrona terminando depois que a célula/coluna dela já foi removida)
        // não pode derrubar o programa inteiro no meio de um evento ao vivo — melhor
        // logar, avisar e continuar do que perder a composição toda.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        // + threads de background e Tasks órfãs (que a linha acima NÃO cobre e que
        // matavam o processo "do nada", sem rastro no log) — relatório em
        // %LocalAppData%\VideoWallRW\crashes\ pra análise posterior.
        CrashReporter.Register();

        // O processo também cria seu próprio device D3D11 (RenderThread) além do que o WPF
        // usa internamente para desenhar suas próprias janelas — em algumas combinações de
        // GPU/driver isso deixa a janela do painel em branco até o primeiro repaint forçado.
        // O painel não tem nada pesado para desenhar (o vídeo vai pelas janelas de saída,
        // não pelo WPF), então cair para software aqui não tem custo perceptível.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        // Instância única: duas cópias do programa brigam pelas saídas e tocam áudio
        // em dobro (som "picotado" e mute que parece não funcionar — cada instância
        // tem sua própria sessão WASAPI).
        bool createdNew;
        try
        {
            _singleInstanceMutex = new Mutex(true, @"Global\VideoWallRW-SingleInstance", out createdNew);
        }
        catch (UnauthorizedAccessException)
        {
            // Another Windows user's session may own the global output lock.
            createdNew = false;
        }
        if (!createdNew)
        {
            if (!e.Args.Contains("--startup", StringComparer.OrdinalIgnoreCase))
                MessageBox.Show(
                    "O RW VideoWall já está aberto neste computador, possivelmente em outra sessão do Windows.\n" +
                    "Use a instância existente para evitar conflito nas saídas e no áudio.",
                    "RW VideoWall", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(0);
            return;
        }

        // Post-mortem ANTES do MarkSessionStart (que sobrescreve o sentinela): se a
        // sessão anterior morreu sem despedida (crash nativo em FFmpeg/D3D11/NDI,
        // kill, queda de energia — casos em que NENHUM handler .NET roda), monta um
        // relatório com a cauda do log anterior + eventos de crash do Windows e avisa
        // o operador onde ele está, pra poder enviar pra análise.
        string? uncleanReport = CrashReporter.ReportUncleanExitIfAny();

        // T7.3: sentinela de sessão — se ainda existir no próximo start, o
        // encerramento anterior não foi limpo (crash), e a UI oferece recuperação.
        AutosaveService.MarkSessionStart();
        _sessionStarted = true;

        // Faxina dos caches (miniaturas/scrub/prévias NDI) a cada 5 dias, em background.
        CacheMaintenance.RunIfDue();

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

        // Descoberta de fontes NDI: fica rodando em background durante toda a sessão
        // (a aba Sources só lê a lista atual) — se o runtime não estiver instalado,
        // IsAvailable fica false e a UI mostra a mensagem de instalação em vez de travar.
        _ndiFinder = new Media.Ndi.NdiFinder();
        _ndiFinder.Start();

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
                Core.Model.NdiSource n => new Media.Ndi.NdiVideoSource(gfxRef, n.SourceName),
                _ => throw new NotSupportedException($"Tipo de fonte {clip.Source.GetType()} não é suportada por esta factory.")
            };
        });

        _render.Start();
        _heartbeat = new HeartbeatClient();

        bool kiosk = e.Args.Any(a => a.Equals("--kiosk", StringComparison.OrdinalIgnoreCase));
        var vm = new MainViewModel(_gfx, _render, kiosk, _ndiFinder);
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

        // Aviso do post-mortem na BARRA DE STATUS, nunca num diálogo modal: um modal
        // no startup trava a interface bem na hora de montar o evento (e aparece também
        // depois de um encerramento forçado ou queda de energia, que não são bugs).
        // O caminho do relatório fica visível e o log guarda o registro completo.
        if (uncleanReport is not null)
            vm.StatusText = $"⚠ A última sessão fechou de forma inesperada — relatório: {uncleanReport}";
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

    private static void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        CrashReporter.Write("UI thread (recuperado — programa seguiu rodando)", e.Exception);
        Log.Error(e.Exception, "Erro não tratado na UI thread — programa segue rodando");
        try
        {
            MessageBox.Show(
                $"Ocorreu um erro inesperado, mas o programa continua rodando:\n{e.Exception.Message}\n\n" +
                "Se algo parecer errado na grade, salve o projeto e reabra pra garantir.",
                "VideoWallRW — erro recuperado", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { /* nem o aviso pode derrubar o handler */ }
        e.Handled = true;
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
        _ndiFinder?.Dispose();
        if (_sessionStarted)
            AutosaveService.MarkSessionClean();
        Log.Information("VideoWallRW encerrado");
        Log.CloseAndFlush();
    }
}
