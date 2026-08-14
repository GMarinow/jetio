using Jetio.Configuration;
using Microsoft.Extensions.Options;

namespace Jetio.Library;

/// <summary>Runs the catalog sync on startup and then on a fixed interval.</summary>
public sealed class LibrarySyncService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SyncOptions _options;
    private readonly ILogger<LibrarySyncService> _logger;

    public LibrarySyncService(
        IServiceScopeFactory scopeFactory,
        IOptions<JetioOptions> options,
        ILogger<LibrarySyncService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value.Sync;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.RunOnStartup)
        {
            await WaitForNextAsync(stoppingToken).ConfigureAwait(false);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var synchronizer = scope.ServiceProvider.GetRequiredService<LibrarySynchronizer>();
                await synchronizer.SyncAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled sync failed; will retry at the next interval");
            }

            await WaitForNextAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task WaitForNextAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromHours(Math.Max(1, _options.IntervalHours));
        _logger.LogInformation("Next sync in {Interval}", interval);

        try
        {
            await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}
