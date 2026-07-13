using System.IO;
using System.Windows;
using Serilog;
using VideoWallRW.Engine;
using VideoWallRW.Engine.Graphics;
using VideoWallRW.Media;
using VideoWallRW.Media.Sources;

namespace VideoWallRW.App;

public partial class App : Application
{
    private GraphicsDevice? _gfx;
    private RenderThread? _render;

    private void OnStartup(object sender, StartupEventArgs e)
    {
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

        _render = new RenderThread(_gfx);

        // Factory injetada: cria VideoFileSource no contexto do App (que tem referência a Media).
        // Isso mantém Engine sem dependência direta de Media.
        var gfxRef = _gfx;
        _render.SetClipFactory(path => new VideoFileSource(gfxRef, path));

        _render.Start();

        var window = new MainWindow(new MainViewModel(_gfx, _render));
        MainWindow = window;
        window.Show();
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        // Ordem importa: parar a thread (que descarta as saídas) antes do device.
        _render?.Stop();
        _gfx?.Dispose();
        Log.Information("VideoWallRW encerrado");
        Log.CloseAndFlush();
    }
}
