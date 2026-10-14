using CodexQuota.Core.Services;
using CodexQuota.Core.Models;

namespace CodexQuota.Core.LocalUsage;

/// <summary>
/// Owns the background lifetime of local daily statistics. Requests are coalesced and
/// scans are single-flight, cancelable and awaited on dispose, so panel or ball
/// interactions can never start file I/O on the UI thread and no owned work survives
/// exit. It is intentionally independent of the official quota refresh: local scanning
/// continues even when the network read fails.
/// </summary>
public sealed class LocalUsageCoordinator : IAsyncDisposable
{
    private readonly LocalUsageScanner _scanner;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _shutdown = new();

    private Task _pending = Task.CompletedTask;
    private bool _requested;
    private bool _running;
    private bool _disposed;

    public LocalUsageCoordinator(LocalUsageScanner scanner) => _scanner = scanner;

    /// <summary>Most recently published snapshot, or null before the first scan completes.</summary>
    public LocalUsageSnapshot? Snapshot { get; private set; }

    /// <summary>Number of completed scans; used by verification only.</summary>
    public int CompletedScans { get; private set; }

    /// <summary>Raised after a scan publishes a new snapshot. Handlers run off the UI thread.</summary>
    public event Action<LocalUsageSnapshot>? Updated;

    /// <summary>
    /// Requests a scan. Requests are coalesced: a request made while a scan is running
    /// runs exactly one more scan afterwards. This is the only entry point for scanning,
    /// so no interaction handler can trigger disk I/O.
    /// </summary>
    public void RequestScan()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _requested = true;
            if (_running)
            {
                return;
            }

            _running = true;
            _pending = Task.Run(RunPendingAsync);
        }
    }

    private async Task RunPendingAsync()
    {
        while (true)
        {
            lock (_sync)
            {
                if (_disposed || !_requested)
                {
                    _running = false;
                    return;
                }

                _requested = false;
            }

            LocalUsageSnapshot snapshot;
            try
            {
                snapshot = await _scanner.ScanAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                lock (_sync) { _running = false; }
                return;
            }
            catch (Exception)
            {
                snapshot = LocalUsageSnapshot.Unavailable(
                    DateOnly.FromDateTime(DateTime.Now),
                    DateTimeOffset.Now,
                    new AppMessage("LocalCoordinatorFailure"));
            }

            Snapshot = snapshot;
            CompletedScans++;
            Updated?.Invoke(snapshot);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task pending;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            pending = _pending;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        try
        {
            await pending.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Cancellation and scan faults are already reflected in the published state.
        }

        _shutdown.Dispose();
        _scanner.Dispose();
    }
}
