using System.Diagnostics;
using System.Globalization;
using System.Text;
using Jetio.Configuration;
using Microsoft.Extensions.Options;

namespace Jetio.Streaming;

/// <param name="SourceUrl">The release, as the streaming server serves it.</param>
/// <param name="SourceContainer">Container the release is in, which decides whether its own
/// subtitle tracks can be copied across.</param>
/// <param name="Tracks">Subtitle files to embed.</param>
/// <param name="SeekTo">Where to start, translated from the client's byte offset.</param>
public sealed record MuxRequest(
    string SourceUrl,
    string SourceContainer,
    IReadOnlyList<SubtitleTrack> Tracks,
    TimeSpan SeekTo);

/// <summary>
/// Repackages a release with its subtitle files muxed in as real tracks, streaming the result
/// straight to the player.
///
/// Nothing is re-encoded — video and audio are copied and only the container is rebuilt — so the
/// cost is bandwidth through jetio rather than CPU. Matroska is the output because it carries
/// SRT and ASS natively, and because every client that matters demuxes it.
/// </summary>
public sealed class SubtitleMuxer
{
    private const int CopyBufferBytes = 128 * 1024;

    private readonly SubtitleOptions _options;
    private readonly ILogger<SubtitleMuxer> _logger;

    public SubtitleMuxer(IOptions<JetioOptions> options, ILogger<SubtitleMuxer> logger)
    {
        _options = options.Value.Subtitles;
        _logger = logger;
    }

    public const string ContentType = "video/x-matroska";

    public async Task StreamAsync(MuxRequest request, Stream destination, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(_options.FfmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in BuildArguments(request))
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

        // stderr must be drained or ffmpeg blocks once the pipe fills, which looks exactly like
        // a stalled stream. Only the tail is kept — it is all that matters when reporting a failure.
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
                    "ffmpeg exited {Code} muxing {Count} subtitle(s): {Error}",
                    process.ExitCode,
                    request.Tracks.Count,
                    errors.ToString().Trim());
            }
        }
        catch (OperationCanceledException)
        {
            // The player closed the connection — a seek, or simply stopping. Expected.
            _logger.LogDebug("Playback connection closed; stopping ffmpeg");
        }
        finally
        {
            Terminate(process);
            await draining.ConfigureAwait(false);
        }
    }

    internal IReadOnlyList<string> BuildArguments(MuxRequest request)
    {
        List<string> args =
        [
            "-hide_banner",
            "-nostdin",
            "-loglevel", "error",
        ];

        // Seeking before the input makes ffmpeg jump in the file rather than decode up to the
        // point, and it has to be repeated per input or the subtitles start at zero and drift.
        var seek = request.SeekTo > TimeSpan.Zero
            ? request.SeekTo.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)
            : null;

        if (seek is not null)
        {
            args.AddRange(["-ss", seek]);
        }

        args.AddRange(["-i", request.SourceUrl]);

        foreach (var track in request.Tracks)
        {
            if (DetectCharset(track.Path) is { } charset)
            {
                args.AddRange(["-sub_charenc", charset]);
            }

            if (seek is not null)
            {
                args.AddRange(["-ss", seek]);
            }

            args.AddRange(["-i", track.Path]);
        }

        args.AddRange(["-map", "0:v:0", "-map", "0:a?"]);

        // External tracks are mapped before the release's own so their output indexes are known,
        // which is what the -metadata flags below address.
        for (var i = 0; i < request.Tracks.Count; i++)
        {
            args.AddRange(["-map", $"{i + 1}:0"]);
        }

        // Only Matroska sources can hand their subtitles over untouched. Copying mov_text out of
        // an mp4 into Matroska is not supported, and would fail the whole mux rather than one track.
        if (IsMatroska(request.SourceContainer))
        {
            args.AddRange(["-map", "0:s?"]);
        }

        args.AddRange(["-c", "copy"]);

        for (var i = 0; i < request.Tracks.Count; i++)
        {
            var track = request.Tracks[i];

            if (track.Language is not null)
            {
                args.AddRange([$"-metadata:s:s:{i}", $"language={track.Language}"]);
            }

            args.AddRange([$"-metadata:s:s:{i}", $"title={track.Title}"]);

            if (IsDefaultTrack(track))
            {
                args.AddRange([$"-disposition:s:{i}", "default"]);
            }
        }

        args.AddRange(["-f", "matroska", "-"]);
        return args;
    }

    private bool IsDefaultTrack(SubtitleTrack track)
    {
        if (string.IsNullOrWhiteSpace(_options.DefaultLanguage) || track.Language is null)
        {
            return false;
        }

        var configured = SubtitleLocator.ExtractLanguage($"x.{_options.DefaultLanguage}.srt")
            ?? _options.DefaultLanguage;

        return string.Equals(configured, track.Language, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMatroska(string container) =>
        container.Equals("mkv", StringComparison.OrdinalIgnoreCase)
        || container.Equals("webm", StringComparison.OrdinalIgnoreCase)
        || container.Equals("matroska", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// ffmpeg assumes UTF-8 and aborts the whole mux on anything else, so a legacy-encoded file
    /// has to be declared. Cyrillic subtitles are still commonly distributed as Windows-1251;
    /// the CP1251 reading is only trusted when it actually yields Cyrillic, so a Western European
    /// file is not mangled into it.
    /// </summary>
    internal static string? DetectCharset(string path)
    {
        byte[] bytes;

        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        try
        {
            new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return null;
        }
        catch (DecoderFallbackException)
        {
            // Not UTF-8. Fall through and work out which legacy encoding it is.
        }

        // Decoding to compare would mean registering the code-pages provider, which .NET does not
        // ship enabled. The byte layout answers it just as well: CP1251 holds Cyrillic across
        // 0xC0-0xFF, whereas in CP1252 that range is accented Latin — common in a word, rare in bulk.
        var nonAscii = bytes.Count(b => b >= 0x80);
        var cyrillicRange = bytes.Count(b => b >= 0xC0);

        return nonAscii > 0 && cyrillicRange >= nonAscii * 0.8 ? "CP1251" : "CP1252";
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
            // The process was killed mid-read; nothing useful left to collect.
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
