using System.Globalization;
using Jetio.Configuration;
using Jetio.Stremio;
using Microsoft.Extensions.Options;

namespace Jetio.Streaming;

/// <summary>
/// Produces the pieces a player asks for: one video segment at a time, and each subtitle file
/// converted to WebVTT.
///
/// Segments are cut on demand rather than by a long-running job. Video and audio are copied, so
/// starting ffmpeg costs almost nothing, and the alternative — one process writing every segment
/// in order — has to be torn down and restarted the moment anyone scrubs. Cutting per segment
/// makes a seek indistinguishable from ordinary playback.
/// </summary>
public sealed class HlsStreamer
{
    public const string SegmentContentType = "video/mp2t";
    public const string SubtitleContentType = "text/vtt";

    private readonly FfmpegRunner _ffmpeg;
    private readonly SubtitleOptions _options;

    public HlsStreamer(FfmpegRunner ffmpeg, IOptions<JetioOptions> options)
    {
        _ffmpeg = ffmpeg;
        _options = options.Value.Subtitles;
    }

    public Task WriteSegmentAsync(
        ResolvedStream resolved,
        int index,
        IReadOnlyList<SubtitleTrack> tracks,
        Stream destination,
        CancellationToken cancellationToken)
    {
        var start = ((double)index * HlsPlaylists.SegmentSeconds)
            .ToString("0.###", CultureInfo.InvariantCulture);

        var length = ((double)HlsPlaylists.SegmentSeconds)
            .ToString("0.###", CultureInfo.InvariantCulture);

        var burn = _options.BurnIn && tracks.Count > 0 ? tracks[0] : null;

        List<string> arguments =
        [
            "-hide_banner", "-nostdin", "-loglevel", "error",

            // Seeking before the input jumps straight to the position instead of decoding up to
            // it, which is what keeps a scrub from pulling the whole torrent to get there.
            "-ss", start,
        ];

        // Absolute timestamps have to survive into the filter graph, or the subtitles filter —
        // which works from the times written in the file — draws the opening line over every
        // segment. Jellyfin's own burn-in path uses the same pairing for the same reason.
        if (burn is not null)
        {
            arguments.Add("-copyts");
        }

        arguments.AddRange(["-i", resolved.Url, "-t", length, "-map", "0:v:0", "-map", "0:a?"]);

        if (burn is null)
        {
            arguments.AddRange(["-c", "copy"]);
        }
        else
        {
            arguments.AddRange(["-vf", BuildSubtitleFilter(burn)]);

            // Drawing into the picture means the video cannot be copied. Audio is re-encoded too:
            // it is cheap next to the video, and it removes the chance of a codec MPEG-TS cannot
            // carry failing the segment after the expensive part has already been done.
            arguments.AddRange(
            [
                "-c:v", "libx264",
                "-preset", "veryfast",
                "-crf", _options.BurnInQuality.ToString(CultureInfo.InvariantCulture),
                "-c:a", "aac",
                "-b:a", "256k",
                "-avoid_negative_ts", "disabled",
            ]);
        }

        // Timestamps restart at zero in every segment otherwise, and the player stitches them
        // into a film that jumps back to the beginning six seconds at a time. With -copyts they
        // are already absolute, so offsetting again would double it.
        if (burn is null)
        {
            arguments.AddRange(["-output_ts_offset", start]);
        }

        arguments.AddRange(["-muxdelay", "0", "-f", "mpegts", "-"]);

        return _ffmpeg.RunAsync(arguments, destination, $"segment {index}", cancellationToken);
    }

    /// <summary>
    /// Filter arguments are parsed, not passed through, so the path has to be escaped twice over:
    /// once for the filter graph's own separators and once for the option value. A library path
    /// with an apostrophe in a film's title breaks this otherwise.
    /// </summary>
    private static string BuildSubtitleFilter(SubtitleTrack track)
    {
        var path = track.Path
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(":", "\\:", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);

        var filter = $"subtitles=f='{path}'";

        return SubtitleEncoding.Detect(track.Path) is { } charset
            ? $"{filter}:charenc={charset}"
            : filter;
    }

    /// <summary>
    /// Conversion happens per request rather than being cached: a subtitle file is small, ffmpeg
    /// converts one in well under a second, and caching would mean deciding when a replaced file
    /// counts as stale.
    /// </summary>
    public Task WriteSubtitleAsync(
        SubtitleTrack track,
        Stream destination,
        CancellationToken cancellationToken)
    {
        List<string> arguments = ["-hide_banner", "-nostdin", "-loglevel", "error"];

        // Anything not already UTF-8 has to be declared, or ffmpeg rejects the file outright.
        if (SubtitleEncoding.Detect(track.Path) is { } charset)
        {
            arguments.AddRange(["-sub_charenc", charset]);
        }

        arguments.AddRange(["-i", track.Path, "-f", "webvtt", "-"]);

        return _ffmpeg.RunAsync(arguments, destination, $"subtitle {track.Title}", cancellationToken);
    }
}
