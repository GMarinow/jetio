namespace Jetio.Library;

/// <summary>
/// Singleton holding cross-run sync state. Kept separate from <see cref="LibrarySynchronizer"/>
/// so the synchronizer itself can stay scoped and get fresh HTTP clients on every run.
/// </summary>
public sealed class SyncState
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsRunning { get; private set; }

    public SyncReport? LastReport { get; private set; }

    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IsRunning = true;
        return new Releaser(this);
    }

    public void Publish(SyncReport report) => LastReport = report;

    private sealed class Releaser : IDisposable
    {
        private readonly SyncState _state;
        private bool _disposed;

        public Releaser(SyncState state) => _state = state;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _state.IsRunning = false;
            _state._gate.Release();
        }
    }
}
