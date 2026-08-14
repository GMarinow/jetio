using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Jetio.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Jetio.Streaming;

/// <param name="Duration">Total running time, used to turn a byte offset into a seek position.</param>
/// <param name="Size">Total bytes, as the streaming server reports them.</param>
public sealed record StreamMetrics(TimeSpan Duration, long Size);

/// <summary>
/// Learns a stream's duration and size, which is the whole of what makes seeking work: players
/// seek a progressive stream by byte offset, and ffmpeg seeks by time. Without both numbers there
/// is no way to translate between them, so the muxer refuses range requests instead of guessing.
///
/// Cached per URL. The probe reads the container header, which on a torrent means waiting for the
/// opening pieces — worth doing once per release, not once per seek.
/// </summary>
public sealed class MediaProbe
{
    private readonly IMemoryCache _cache;
    private readonly SubtitleOptions _options;
    private readonly ILogger<MediaProbe> _logger;

    public MediaProbe(IMemoryCache cache, IOptions<JetioOptions> options, ILogger<MediaProbe> logger)
    {
        _cache = cache;
        _options = options.Value.Subtitles;
        _logger = logger;
    }

    public async Task<StreamMetrics?> GetAsync(string url, CancellationToken cancellationToken)
    {
        var key = $"probe:{url}";

        if (_cache.TryGetValue(key, out StreamMetrics? cached))
        {
            return cached;
        }

        var metrics = await ProbeAsync(url, cancellationToken).ConfigureAwait(false);

        // Failures are cached briefly too. A torrent with no peers yet fails every probe, and
        // retrying on each request would leave several ffprobes running against a dead stream.
        _cache.Set(key, metrics, metrics is null ? TimeSpan.FromMinutes(1) : TimeSpan.FromHours(6));
        return metrics;
    }

    private async Task<StreamMetrics?> ProbeAsync(string url, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(_options.FfprobePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in new[]
                 {
                     "-v", "quiet",
                     "-print_format", "json",
                     "-show_format",
                     "-i", url,
                 })
        {
            start.ArgumentList.Add(argument);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _options.ProbeTimeoutSeconds)));

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            var json = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
            {
                _logger.LogWarning("ffprobe exited {Code} for {Url}", process.ExitCode, url);
                return null;
            }

            return Parse(json);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("ffprobe timed out after {Seconds}s for {Url}", _options.ProbeTimeoutSeconds, url);
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogError(ex, "Could not run {Ffprobe}. Is it installed?", _options.FfprobePath);
            return null;
        }
    }

    private static StreamMetrics? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("format", out var format))
        {
            return null;
        }

        if (!TryReadNumber(format, "duration", out var seconds) || seconds <= 0)
        {
            return null;
        }

        // Size is absent for some inputs; without it a byte offset cannot be placed on the
        // timeline, so this counts as a failed probe rather than a partial answer.
        if (!TryReadNumber(format, "size", out var size) || size <= 0)
        {
            return null;
        }

        return new StreamMetrics(TimeSpan.FromSeconds(seconds), (long)size);
    }

    private static bool TryReadNumber(JsonElement element, string name, out double value)
    {
        value = 0;

        if (!element.TryGetProperty(name, out var node))
        {
            return false;
        }

        return node.ValueKind switch
        {
            JsonValueKind.Number => node.TryGetDouble(out value),
            JsonValueKind.String => double.TryParse(
                node.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value),
            _ => false,
        };
    }
}
