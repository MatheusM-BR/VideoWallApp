using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Serilog;

namespace VideoWallRW.Media;

/// <summary>
/// Inicialização global do FFmpeg. Chamar uma vez no startup, antes de criar qualquer decoder.
/// </summary>
public static class FFmpegBootstrap
{
    private static bool s_initialized;


    /// <summary>
    /// Configura o caminho das DLLs nativas do FFmpeg e registra o callback de log.
    /// </summary>
    /// <param name="dllFolder">
    /// Caminho absoluto para a pasta contendo as DLLs do FFmpeg (avcodec-*.dll etc.).
    /// Tipicamente: AppContext.BaseDirectory — as DLLs são copiadas pelo csproj.
    /// </param>
    public static void Initialize(string dllFolder)
    {
        if (s_initialized) return;
        s_initialized = true;

        ffmpeg.RootPath = dllFolder;

        // Redirecionar log do FFmpeg para silêncio (ou Serilog).
        // A API de callback do AutoGen 7.x usa um struct wrapper especial;
        // simplificamos suprimindo o log padrão via av_log_set_level.
        try
        {
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR); // só erros no log nativo
        }
        catch
        {
            // Se falhar (DLL ausente), a exceção na linha abaixo vai capturar.
        }

        try
        {
            Log.Information("FFmpeg inicializado: {Version}", ffmpeg.av_version_info());
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Falha ao inicializar o FFmpeg — DLLs não encontradas em '{Folder}'", dllFolder);
            throw;
        }
    }
}
