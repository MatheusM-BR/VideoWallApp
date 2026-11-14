using System.IO;
using Serilog;

namespace VideoWallRW.App;

/// <summary>
/// Faxina periódica dos caches do programa (miniaturas, frames de scrub, prévias de
/// NDI). Esses arquivos são descartáveis — são regerados sob demanda — mas acumulam
/// indefinidamente ao longo de meses de operação, ocupando disco e deixando a pasta
/// de miniaturas lenta de varrer. Roda no máximo uma vez a cada 5 dias (marca a data
/// da última limpeza num arquivo), sempre em background pra não atrasar a abertura.
/// </summary>
public static class CacheMaintenance
{
    /// <summary>Intervalo entre faxinas (pedido do operador: "limpar o cache a cada 5 dias").</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromDays(5);

    /// <summary>Arquivos de cache mais novos que isto são poupados — são justamente os
    /// do projeto em uso agora, e apagá-los só forçaria regeração imediata.</summary>
    private static readonly TimeSpan KeepRecent = TimeSpan.FromDays(2);

    private static string BaseDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoWallRW");

    private static string StampPath => Path.Combine(BaseDir, "cache-cleanup.stamp");

    /// <summary>Pastas varridas na faxina.</summary>
    private static IEnumerable<string> CacheDirs =>
    [
        Path.Combine(BaseDir, "thumbs"),                                  // miniaturas de mídia
        Path.Combine(Path.GetTempPath(), "VideoWallRW", "ndi-thumbs"),    // prévias das fontes NDI
        Path.Combine(Path.GetTempPath(), "VideoWallRW", "scrub"),         // frames extraídos no scrub
    ];

    /// <summary>
    /// Chamar no startup. Não bloqueia: se estiver na hora, a faxina roda numa task de
    /// baixa prioridade. Erros são engolidos — limpeza de cache jamais pode atrapalhar
    /// um evento ao vivo.
    /// </summary>
    public static void RunIfDue()
    {
        try
        {
            if (!IsDue()) return;
            _ = Task.Run(() =>
            {
                int removed = 0;
                long freed = 0;
                var cutoff = DateTime.UtcNow - KeepRecent;

                foreach (var dir in CacheDirs)
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
                    {
                        // LastWriteTime (não CreationTime): um cache reaproveitado hoje
                        // continua "em uso" mesmo tendo sido criado há meses.
                        if (file.LastWriteTimeUtc > cutoff) continue;
                        try
                        {
                            long size = file.Length;
                            file.Delete();
                            removed++;
                            freed += size;
                        }
                        catch { /* arquivo em uso: fica pra próxima faxina */ }
                    }
                }

                MarkDone();
                Log.Information("Faxina de cache: {N} arquivos removidos ({MB:F1} MB liberados)",
                    removed, freed / 1024.0 / 1024.0);
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Falha ao agendar a faxina de cache (ignorado)");
        }
    }

    private static bool IsDue()
    {
        try
        {
            if (!File.Exists(StampPath))
            {
                // Primeira execução: só marca a data, sem apagar nada. Assim a faxina
                // acontece a partir do 5º dia de uso, não logo na primeira abertura.
                MarkDone();
                return false;
            }
            var last = File.GetLastWriteTimeUtc(StampPath);
            return DateTime.UtcNow - last >= Interval;
        }
        catch { return false; }
    }

    private static void MarkDone()
    {
        try
        {
            Directory.CreateDirectory(BaseDir);
            File.WriteAllText(StampPath, DateTimeOffset.Now.ToString("O"));
        }
        catch { /* melhor esforço */ }
    }
}
