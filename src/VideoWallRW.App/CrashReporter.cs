using System.IO;
using System.Text;
using Serilog;

namespace VideoWallRW.App;

/// <summary>
/// Relatório de crash em arquivo (pedido do operador: "o programa está fechando —
/// crie um crash report para analisarmos"). Cobre TODOS os jeitos de um processo
/// morrer, porque cada um deixa (ou não) rastro de um jeito diferente:
///  - UI thread (DispatcherUnhandledException): recuperável — App.xaml.cs marca
///    Handled e segue rodando, mas o relatório fica salvo;
///  - qualquer outra thread (AppDomain.UnhandledException): o runtime SEMPRE mata o
///    processo depois do handler — pelo menos morre deixando o relatório;
///  - Tasks órfãs (TaskScheduler.UnobservedTaskException): não mata, mas registra;
///  - crash NATIVO (violação de acesso em FFmpeg/D3D11/NDI, stack overflow, kill):
///    NENHUM handler .NET roda — por isso o <see cref="ReportUncleanExitIfAny"/>,
///    chamado no próximo start: se o sentinela de sessão ainda existe, a sessão
///    anterior morreu sem despedida, e o relatório é montado post-mortem com a cauda
///    do log anterior + os eventos de crash do Windows (Event Log).
/// Relatórios ficam em DOIS lugares (o que existir/der permissão): na pasta
/// "crashes" AO LADO DO EXE (fácil de achar/enviar em qualquer máquina) e em
/// %LocalAppData%\VideoWallRW\crashes. Mantém os 30 últimos em cada.
/// </summary>
public static class CrashReporter
{
    private const int MaxReports = 30;

    /// <summary>Pastas candidatas, em ordem de preferência de exibição pro usuário.</summary>
    public static IReadOnlyList<string> CrashDirs { get; } =
    [
        Path.Combine(AppContext.BaseDirectory, "crashes"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VideoWallRW", "crashes"),
    ];

    public static void Register()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("AppDomain (thread de background — processo vai encerrar)", e.ExceptionObject as Exception, fatal: true);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("Task órfã (fire-and-forget sem await)", e.Exception);
            e.SetObserved();
        };
        // O caminho da UI thread já é tratado em App.OnDispatcherUnhandledException,
        // que chama Write() diretamente antes de marcar Handled.
    }

    /// <summary>
    /// Chamar no startup ANTES de AutosaveService.MarkSessionStart() (que sobrescreve
    /// o sentinela): se a sessão anterior morreu sem limpar o sentinela — crash
    /// nativo, kill, queda de energia — monta um relatório post-mortem com tudo que
    /// dá pra recuperar. Retorna o caminho do relatório (pra UI avisar o operador),
    /// ou null se a sessão anterior encerrou limpa.
    /// </summary>
    public static string? ReportUncleanExitIfAny()
    {
        try
        {
            if (!Core.Persistence.AutosaveService.WasUncleanShutdown())
                return null;

            var sb = new StringBuilder();
            AppendHeader(sb, "Sessão anterior encerrou de forma SUJA (crash nativo, kill ou queda de energia — nenhum handler .NET chegou a rodar)");

            sb.AppendLine("─── Cauda do log da sessão anterior ───");
            sb.AppendLine(TryReadPreviousLogTail() ?? "(nenhum log encontrado)");
            sb.AppendLine();
            sb.AppendLine("─── Eventos de crash do Windows (últimas 48h, VideoWallRW) ───");
            sb.AppendLine(TryReadWindowsCrashEvents() ?? "(nenhum evento encontrado — morte sem exceção: kill externo, queda de energia ou stack overflow silencioso)");

            return WriteReport("unclean", sb.ToString());
        }
        catch { return null; }
    }

    /// <summary>Escreve o relatório de forma defensiva — um erro AQUI nunca pode
    /// esconder o crash original nem derrubar nada por conta própria. <paramref
    /// name="fatal"/> = o processo vai morrer logo em seguida (só aí o log é
    /// descarregado à força; nos caminhos recuperáveis o Serilog segue vivo).</summary>
    public static void Write(string source, Exception? ex, bool fatal = false)
    {
        try
        {
            var sb = new StringBuilder();
            AppendHeader(sb, source);
            sb.AppendLine("─── Exceção ───");
            sb.AppendLine(ex?.ToString() ?? "(objeto de exceção nulo/não-Exception)");

            string? file = WriteReport("crash", sb.ToString());
            Log.Error(ex, "Crash reportado ({Source}) — relatório: {File}", source, file ?? "(falhou ao gravar)");
            if (fatal)
                Log.CloseAndFlush(); // garante que o Serilog descarregou antes do processo morrer
        }
        catch { /* nunca deixar o reporter causar problema por cima do crash original */ }
    }

    private static void AppendHeader(StringBuilder sb, string source)
    {
        sb.AppendLine("═══ VideoWallRW — Relatório de Crash ═══");
        sb.AppendLine($"Quando:    {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}");
        sb.AppendLine($"Origem:    {source}");
        sb.AppendLine($"Versão:    {typeof(CrashReporter).Assembly.GetName().Version} " +
                      $"(build de {TryGetBuildTime():yyyy-MM-dd HH:mm} — {AppContext.BaseDirectory})");
        sb.AppendLine($"SO:        {Environment.OSVersion} (64-bit: {Environment.Is64BitOperatingSystem})");
        sb.AppendLine($".NET:      {Environment.Version}");
        sb.AppendLine($"Memória:   {Environment.WorkingSet / 1024 / 1024} MB (working set)");
        try { sb.AppendLine($"Threads:   {System.Diagnostics.Process.GetCurrentProcess().Threads.Count}"); }
        catch { /* info opcional */ }
        sb.AppendLine();
    }

    private static DateTime TryGetBuildTime()
    {
        try { return File.GetLastWriteTime(typeof(CrashReporter).Assembly.Location); }
        catch { return DateTime.MinValue; }
    }

    /// <summary>Grava em todas as pastas candidatas (a do exe pode ser somente-leitura
    /// em Program Files — aí só a de LocalAppData funciona). Retorna o primeiro
    /// caminho gravado com sucesso.</summary>
    private static string? WriteReport(string prefix, string content)
    {
        string name = $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt";
        string? firstWritten = null;
        foreach (var dir in CrashDirs)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, name);
                File.WriteAllText(file, content);
                firstWritten ??= file;
                Prune(dir);
            }
            catch { /* tenta a próxima pasta */ }
        }
        return firstWritten;
    }

    private static string? TryReadPreviousLogTail()
    {
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VideoWallRW", "logs");
            var newest = new DirectoryInfo(logDir).GetFiles("app-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (newest is null) return null;

            // FileShare.ReadWrite: o Serilog da sessão atual pode já estar com o
            // arquivo do dia aberto pra escrita.
            using var fs = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line) lines.Add(line);
            return $"[{newest.Name}]\n" + string.Join('\n', lines.TakeLast(80));
        }
        catch (Exception ex) { return $"(falha ao ler o log: {ex.Message})"; }
    }

    private static string? TryReadWindowsCrashEvents()
    {
        try
        {
            var sb = new StringBuilder();
            var query = new System.Diagnostics.Eventing.Reader.EventLogQuery(
                "Application", System.Diagnostics.Eventing.Reader.PathType.LogName,
                // 1000 = Application Error (crash nativo), 1026 = .NET Runtime (exceção fatal)
                "*[System[(EventID=1000 or EventID=1026) and TimeCreated[timediff(@SystemTime) <= 172800000]]]")
            {
                // SEM isto o leitor devolve os eventos MAIS ANTIGOS primeiro e o relatório
                // acabava mostrando o crash de dias atrás em vez do que acabou de acontecer
                // — inútil pra diagnóstico. Do mais novo pro mais velho.
                ReverseDirection = true,
            };
            using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(query);
            int count = 0;
            while (reader.ReadEvent() is { } record && count < 6)
            {
                using (record)
                {
                    string desc = record.FormatDescription() ?? "";
                    if (!desc.Contains("VideoWallRW", StringComparison.OrdinalIgnoreCase)) continue;
                    sb.AppendLine($"[{record.TimeCreated:yyyy-MM-dd HH:mm:ss}] {record.ProviderName} (evento {record.Id})");
                    sb.AppendLine(desc.Length > 1500 ? desc[..1500] + "…" : desc);
                    sb.AppendLine();
                    count++;
                }
            }
            return count > 0 ? sb.ToString() : null;
        }
        catch (Exception ex) { return $"(falha ao consultar o Event Log: {ex.Message})"; }
    }

    private static void Prune(string dir)
    {
        var files = new DirectoryInfo(dir).GetFiles("*.txt")
            .OrderByDescending(f => f.CreationTimeUtc).Skip(MaxReports);
        foreach (var f in files)
        {
            try { f.Delete(); } catch { /* melhor esforço */ }
        }
    }
}
