using System.Diagnostics;
using System.IO.Pipes;

namespace VideoWallRW.Watchdog;

/// <summary>
/// Supervisor de processo (Fase 8 T8.3): mantém o VideoWallRW.App rodando sem
/// operador presente. Sozinho, sem dependência de nenhum outro projeto — se o app
/// travar de um jeito que corrompa estado compartilhado, o watchdog não pode
/// carregar o mesmo problema.
/// </summary>
internal static class Program
{
    private const string PipeName = "vwrw-heartbeat";
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FlapWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FlapBackoff = TimeSpan.FromSeconds(60);
    private const int FlapThreshold = 3;

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VideoWallRW", "logs", "watchdog.log");

    private static async Task Main(string[] args)
    {
        string exePath = ResolveAppExePath(args);
        if (!File.Exists(exePath))
        {
            WriteLog($"App não encontrado em '{exePath}'. Encerrando watchdog.");
            return;
        }

        var restarts = new List<DateTime>();

        while (true)
        {
            restarts.Add(DateTime.Now);
            restarts.RemoveAll(t => DateTime.Now - t > FlapWindow);
            if (restarts.Count > FlapThreshold)
            {
                WriteLog($"{restarts.Count} reinícios em {FlapWindow.TotalMinutes:F0} min — " +
                    $"aguardando {FlapBackoff.TotalSeconds:F0}s antes de tentar de novo (anti-flapping).");
                await Task.Delay(FlapBackoff);
            }

            await RunOnceAsync(exePath);
        }
    }

    private static string ResolveAppExePath(string[] args)
    {
        if (args.Length > 0 && File.Exists(args[0]))
            return args[0];

        // Layout padrão de publicação: watchdog e app no mesmo diretório de saída.
        return Path.Combine(AppContext.BaseDirectory, "VideoWallRW.App.exe");
    }

    private static async Task RunOnceAsync(string exePath)
    {
        WriteLog($"Iniciando '{exePath}' --kiosk");
        using var process = Process.Start(new ProcessStartInfo(exePath, "--kiosk")
        {
            UseShellExecute = false,
        });
        if (process is null)
        {
            WriteLog("Falha ao iniciar o processo do app.");
            return;
        }

        using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var connectTask = pipe.WaitForConnectionAsync();
        var startupTimeout = Task.Delay(StartupGrace);
        var completed = await Task.WhenAny(connectTask, startupTimeout, WaitForExitAsync(process));

        if (process.HasExited)
        {
            WriteLog($"App encerrou sozinho durante a inicialização (código {process.ExitCode}).");
            return;
        }
        if (completed == startupTimeout)
        {
            WriteLog("App não conectou o heartbeat a tempo — encerrando e reiniciando.");
            KillProcess(process);
            return;
        }

        WriteLog("Heartbeat conectado.");
        var buffer = new byte[1];
        while (true)
        {
            var readTask = pipe.ReadAsync(buffer).AsTask();
            var timeoutTask = Task.Delay(HeartbeatTimeout);
            var exitTask = WaitForExitAsync(process);
            var winner = await Task.WhenAny(readTask, timeoutTask, exitTask);

            if (winner == exitTask)
            {
                WriteLog($"App encerrou (código {process.ExitCode}).");
                return;
            }
            if (winner == timeoutTask)
            {
                WriteLog("Sem heartbeat por 10s — app travado. Encerrando e reiniciando.");
                KillProcess(process);
                return;
            }

            int read = await readTask;
            if (read == 0)
            {
                WriteLog("Pipe de heartbeat fechado pelo app — encerrando e reiniciando.");
                KillProcess(process);
                return;
            }
        }
    }

    private static Task WaitForExitAsync(Process process)
    {
        var tcs = new TaskCompletionSource();
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => tcs.TrySetResult();
        if (process.HasExited) tcs.TrySetResult();
        return tcs.Task;
    }

    private static void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            WriteLog($"Falha ao encerrar processo travado: {ex.Message}");
        }
    }

    private static void WriteLog(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch { /* log é best-effort — não pode derrubar o watchdog */ }
    }
}
