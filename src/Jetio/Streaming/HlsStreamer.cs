using System.Globalization;
using Jetio.Stremio;

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

    public HlsStreamer(FfmpegRunner ffmpeg)
    {
        _ffmpeg = ffmpeg;
    }

    public Task WriteSegmentAsync(
        ResolvedStream resolved,
        int index,
        Stream destination,
        CancellationToken cancellationToken)
    {
        var start = ((double)index * HlsPlaylists.SegmentSeconds)
            .ToString("0.###", CultureInfo.InvariantCulture);

        var length = ((double)HlsPlaylists.SegmentSeconds)
            .ToString("0.###", CultureInfo.InvariantCulture);

        List<string> arguments =
        [
            "-hide_banner", "-nostdin", "-loglevel", "error",

            // Seeking before the input jumps straight to the position instead of decoding up to
            // it, which is what keeps a scrub from pulling the whole torrent to get there.
            "-ss", start,
            "-i", resolved.Url,
            "-t", length,

            "-map", "0:v:0",
            "-map", "0:a?",
            "-c", "copy",

            // Timestamps restart at zero in every segment otherwise, and the player stitches them
            // into a film that jumps back to the beginning six seconds at a time.
            "-output_ts_offset", start,
            "-muxdelay", "0",
            "-f", "mpegts",
            "-",
        ];

        return _ffmpeg.RunAsync(arguments, destination, $"segment {index}", cancellationToken);
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
