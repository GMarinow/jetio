using System.Diagnostics;
using System.Text;
using Jetio.Configuration;
using Microsoft.Extensions.Options;

namespace Jetio.Streaming;

/// <summary>
/// Runs ffmpeg and pipes its output straight to the caller, without ever landing on disk.
///
/// The two details that matter are easy to get wrong and hard to diagnose afterwards: stderr must
/// be drained or ffmpeg blocks once that pipe fills — indistinguishable from a stalled stream —
/// and the process must be killed when the player disconnects, or a seek leaves an orphan ffmpeg
/// pulling the torrent for the rest of the film.
/// </summary>
public sealed class FfmpegRunner
{
    private const int CopyBufferBytes = 128 * 1024;

    private readonly SubtitleOptions _options;
    private readonly ILogger<FfmpegRunner> _logger;

    public FfmpegRunner(IOptions<JetioOptions> options, ILogger<FfmpegRunner> logger)
    {
        _options = options.Value.Subtitles;
        _logger = logger;
    }

    public async Task RunAsync(
        IReadOnlyList<string> arguments,
        Stream destination,
        string what,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(_options.FfmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("ffmpeg did not start");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogError(ex, "Could not run {Ffmpeg}. Is it installed in the image?", _options.FfmpegPath);
            throw;
        }

        var errors = new StringBuilder();
        var draining = DrainAsync(process.StandardError, errors);

        try
        {
            await process.StandardOutput.BaseStream
                .CopyToAsync(destination, CopyBufferBytes, cancellationToken)
                .ConfigureAwait(false);

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                _logger.LogWarning(
                    "ffmpeg exited {Code} for {What}: {Error}",
                    process.ExitCode,
                    what,
                    errors.ToString().Trim());
            }
        }
        catch (OperationCanceledException)
        {
            // The player moved on — a scrub, or simply stopping. Expected, and frequent.
            _logger.LogDebug("Connection closed during {What}; stopping ffmpeg", what);
        }
        finally
        {
            Terminate(process);
            await draining.ConfigureAwait(false);
        }
    }

    private static async Task DrainAsync(StreamReader reader, StringBuilder sink)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (sink.Length < 4000)
                {
                    sink.AppendLine(line);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Killed mid-read; nothing useful left to collect.
        }
    }

    private void Terminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            _logger.LogDebug(ex, "ffmpeg had already exited");
        }
    }
}
