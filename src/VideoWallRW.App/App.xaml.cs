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

namespace VideoWallRW.App;

public partial class App : Application
{
    private GraphicsDevice? _gfx;
    private RenderThread? _render;
    private AudioEngine? _audio;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        // O processo também cria seu próprio device D3D11 (RenderThread) além do que o WPF
        // usa internamente para desenhar suas próprias janelas — em algumas combinações de
        // GPU/driver isso deixa a janela do painel em branco até o primeiro repaint forçado.
        // O painel não tem nada pesado para desenhar (o vídeo vai pelas janelas de saída,
        // não pelo WPF), então cair para software aqui não tem custo perceptível.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

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

        var window = new MainWindow(new MainViewModel(_gfx, _render));
        MainWindow = window;
        window.Show();
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
        _render?.Stop();
        _audio?.Dispose();
        _gfx?.Dispose();
        Log.Information("VideoWallRW encerrado");
        Log.CloseAndFlush();
    }
}
