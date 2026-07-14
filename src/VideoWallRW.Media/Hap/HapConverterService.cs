using System.Diagnostics;
using System.Text.RegularExpressions;
using Serilog;

namespace VideoWallRW.Media.Hap;

public enum HapConverterPreset { Hap1, HapAlpha, HapQ }

/// <summary>
/// Conversor em lote para HAP via processo externo `ffmpeg.exe` (build full/GPL —
/// nunca linkado, só invocado como CLI). Procura em
/// <c>third_party\ffmpeg-cli\ffmpeg.exe</c> relativo ao executável, com fallback
/// para `ffmpeg` no PATH.
/// </summary>
public static class HapConverterService
{
    private static readonly Regex TimeRegex = new(@"time=(\d+):(\d+):(\d+\.\d+)", RegexOptions.Compiled);

    public static string? FindFfmpegExecutable()
    {
        string bundled = Path.Combine(AppContext.BaseDirectory, "third_party", "ffmpeg-cli", "ffmpeg.exe");
        if (File.Exists(bundled))
            return bundled;

        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        foreach (var dir in (pathEnv ?? "").Split(Path.PathSeparator))
        {
            try
            {
                string candidate = Path.Combine(dir, "ffmpeg.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException) { /* entrada de PATH inválida — ignora */ }
        }
        return null;
    }

    public static async Task ConvertAsync(string ffmpegPath, string inputPath, string outputPath,
        HapConverterPreset preset, IProgress<double>? progress, CancellationToken ct)
    {
        double totalSeconds = await ProbeDurationAsync(ffmpegPath, inputPath, ct);

        string formatArg = preset switch
        {
            HapConverterPreset.Hap1 => "hap",
            HapConverterPreset.HapAlpha => "hap_alpha",
            HapConverterPreset.HapQ => "hap_q",
            _ => "hap",
        };

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var psi = new ProcessStartInfo(ffmpegPath)
        {
            ArgumentList =
            {
                "-y", "-i", inputPath,
                "-c:v", "hap", "-format", formatArg,
                outputPath,
            },
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Não foi possível iniciar o ffmpeg.");

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            var match = TimeRegex.Match(e.Data);
            if (match.Success && totalSeconds > 0)
            {
                double t = int.Parse(match.Groups[1].Value) * 3600
                    + int.Parse(match.Groups[2].Value) * 60
                    + double.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
                progress?.Report(Math.Clamp(t / totalSeconds, 0, 1));
            }
        };
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg saiu com código {process.ExitCode} ao converter '{inputPath}'.");

        progress?.Report(1.0);
        Log.Information("Convertido para HAP: {Input} -> {Output} ({Preset})", inputPath, outputPath, preset);
    }

    private static async Task<double> ProbeDurationAsync(string ffmpegPath, string inputPath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpegPath)
        {
            ArgumentList = { "-i", inputPath },
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi);
        if (process is null) return 0;
        string stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        var match = Regex.Match(stderr, @"Duration:\s*(\d+):(\d+):(\d+\.\d+)");
        if (!match.Success) return 0;
        return int.Parse(match.Groups[1].Value) * 3600
            + int.Parse(match.Groups[2].Value) * 60
            + double.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
