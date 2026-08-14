using Jetio.Configuration;
using Jetio.Jellyfin;
using Microsoft.Extensions.Options;

namespace Jetio.Library;

/// <summary>
/// Asks Jellyfin to analyse each newly added title once.
///
/// Jellyfin will not otherwise look inside a .strm's stream, leaving it with no container,
/// codec or bitrate — and with nothing to reason about it simply hands the URL to the client.
/// That is why client-side quality limits appear to do nothing on these items, and why
/// subtitle tracks embedded in a release never show up.
///
/// One title at a time, deliberately: each analysis makes Jellyfin pull the opening chunk of
/// a torrent, and running them in parallel would start several downloads at once.
/// </summary>
public sealed class MediaAnalysisService : BackgroundService
{
    private readonly MediaAnalysisQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<JetioOptions> _options;
    private readonly ILogger<MediaAnalysisService> _logger;

    public MediaAnalysisService(
        MediaAnalysisQueue queue,
        IServiceScopeFactory scopeFactory,
        IOptions<JetioOptions> options,
        ILogger<MediaAnalysisService> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await AnalyzeAsync(request, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Analysis failed for {ImdbId}", request.ImdbId);
            }
        }
    }

    private async Task AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken)
    {
        var options = _options.Value.Jellyfin;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var jellyfin = scope.ServiceProvider.GetRequiredService<JellyfinClient>();

        var attempts = Math.Max(1, options.AnalyzeMaxAttempts);
        var delay = TimeSpan.FromSeconds(Math.Max(1, options.AnalyzeDelaySeconds));

        // The item does not exist until Jellyfin's scan reaches it, so poll rather than assume.
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var itemId = await jellyfin.FindItemIdAsync(request.ImdbId, cancellationToken).ConfigureAwait(false);

            if (itemId is not null)
            {
                var analyzed = await jellyfin.AnalyzeItemAsync(itemId, cancellationToken).ConfigureAwait(false);

                if (analyzed)
                {
                    _logger.LogInformation(
                        "Asked Jellyfin to analyse {Name} ({ImdbId})",
                        request.Name ?? request.ImdbId,
                        request.ImdbId);
                }

                return;
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogWarning(
            "{ImdbId} never appeared in Jellyfin after {Seconds}s; skipping analysis. "
            + "Is the library pointed at the folder jetio writes to?",
            request.ImdbId,
            attempts * delay.TotalSeconds);
    }
}
