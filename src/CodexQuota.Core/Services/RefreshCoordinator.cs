using CodexQuota.Core.Models;
using CodexQuota.Core.Providers;

namespace CodexQuota.Core.Services;

/// <summary>
/// Serializes all quota refreshes into one owner of published <see cref="RefreshState"/>.
/// It performs the first fetch immediately, then one fetch per interval, never
/// overlaps cycles, coalesces duplicate manual requests while a cycle is running,
/// applies bounded backoff to transient failures, and marks retained data stale
/// honestly instead of inventing fresh values.
/// </summary>
public sealed class RefreshCoordinator : IAsyncDisposable
{
    private static readonly int[] RetryDelaysSeconds = [30, 60, 120, 240, 300];

    private readonly IQuotaProvider _provider;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _scheduleChanged = new(0, 1);
    private readonly CancellationTokenSource _shutdown = new();

    private TimeSpan _interval;
    private CancellationTokenSource? _loopCancellation;
    private Task? _loopTask;
    private string? _accountKey;
    private int _consecutiveFailures;
    private bool _disposed;

    public RefreshCoordinator(IQuotaProvider provider, TimeProvider? timeProvider = null, TimeSpan? interval = null)
    {
        _provider = provider;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _interval = interval ?? TimeSpan.FromSeconds(WidgetSettings.DefaultIntervalSeconds);
        StaleAfter = TimeSpan.FromTicks(_interval.Ticks * 3);
        State = RefreshState.Initial(Today());
        if (provider is IAccountChangeSource accountChanges)
        {
            accountChanges.AccountChanged += InvalidateAccount;
        }
    }

    /// <summary>Most recently published state.</summary>
    public RefreshState State { get; private set; }

    /// <summary>Age after which a fresh snapshot is considered stale even without a reported failure.</summary>
    public TimeSpan StaleAfter { get; private set; }

    /// <summary>Current refresh interval.</summary>
    public TimeSpan Interval => _interval;

    /// <summary>Raised after a new state transition is published.</summary>
    public event Action<RefreshState>? StateChanged;

    /// <summary>Updates the refresh interval without creating a second loop.</summary>
    public void UpdateInterval(TimeSpan interval)
    {
        _interval = interval <= TimeSpan.Zero ? TimeSpan.FromSeconds(WidgetSettings.DefaultIntervalSeconds) : interval;
        StaleAfter = TimeSpan.FromTicks(_interval.Ticks * 3);
        Publish(State with { NextScheduledFetch = _timeProvider.GetUtcNow() + _interval });
        WakeSchedule();
    }

    /// <summary>Starts the immediate fetch and the periodic loop. Idempotent.</summary>
    public Task StartAsync()
    {
        if (_loopTask is not null || _disposed)
        {
            return Task.CompletedTask;
        }

        var cancellation = new CancellationTokenSource();
        _loopCancellation = cancellation;
        _loopTask = Task.Run(() => RunLoopAsync(cancellation.Token));
        return Task.CompletedTask;
    }

    /// <summary>Stops the periodic loop. Safe to call multiple times.</summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? cancellation = _loopCancellation;
        Task? loop = _loopTask;
        _loopCancellation = null;
        _loopTask = null;

        if (cancellation is null || loop is null)
        {
            return;
        }

        await cancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        cancellation.Dispose();
    }

    /// <summary>
    /// Runs one refresh cycle, waiting for any in-flight cycle to finish first. Used
    /// by tests and by explicit refresh commands.
    /// </summary>
    public async Task RefreshOnceAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        cancellationToken = linked.Token;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RunCycleAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Requests a refresh, dropping the request when a cycle is already running so
    /// scheduled and manual requests never overlap. Returns whether a cycle ran.
    /// </summary>
    public async Task<bool> RequestRefreshAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        cancellationToken = linked.Token;
        if (!_gate.Wait(0))
        {
            return false;
        }

        try
        {
            await RunCycleAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Marks a fresh snapshot stale once it is older than three intervals, even when
    /// no failure has been reported.
    /// </summary>
    public void EvaluateStaleness()
    {
        if (State.DailyUsage is { } daily && daily.RequestedDate != Today())
        {
            Publish(State with { DailyUsage = DailyUsageResult.Missing(Today()) });
        }
        if (State.Status != RefreshStatus.Fresh || State.LastSuccess is not { } lastSuccess)
        {
            return;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (now - lastSuccess >= StaleAfter || HasExpiredReset(State.LastQuota, now))
        {
            Publish(State with
            {
                Status = RefreshStatus.Stale,
                StatusText = new AppMessage("QuotaExpired").Format(),
                StatusMessage = new AppMessage("QuotaExpired"),
            });
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        bool first = true;
        while (!cancellationToken.IsCancellationRequested)
        {
            EvaluateStaleness();
            if (first || State.NextScheduledFetch is null || State.NextScheduledFetch <= _timeProvider.GetUtcNow())
            {
                try
                {
                    await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        if (first || State.NextScheduledFetch <= _timeProvider.GetUtcNow())
                        {
                            await RunCycleAsync(cancellationToken).ConfigureAwait(false);
                        }
                        first = false;
                    }
                    finally { _gate.Release(); }
                }
                catch (OperationCanceledException) { break; }
            }

            EvaluateStaleness();
            DateTimeOffset now = _timeProvider.GetUtcNow();
            DateTimeOffset next = State.NextScheduledFetch ?? now + _interval;
            // Calendar invalidation and quota resets need one-shot deadlines, not polling.
            DateTime localMidnight = _timeProvider.GetLocalNow().Date.AddDays(1);
            DateTimeOffset midnight = new(TimeZoneInfo.ConvertTimeToUtc(localMidnight, _timeProvider.LocalTimeZone), TimeSpan.Zero);
            next = next < midnight ? next : midnight;
            if (State.Status == RefreshStatus.Fresh && State.LastQuota is { } quota)
            {
                foreach (QuotaWindow window in QuotaProjection.SelectMainBucket(quota)?.Windows ?? [])
                {
                    if (window.ResetsAt is { } reset && reset > now && reset < next) { next = reset; }
                }
            }
            TimeSpan delay = next > now ? next - now : TimeSpan.Zero;
            try
            {
                using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Task timer = Task.Delay(delay, _timeProvider, waitCancellation.Token);
                Task changed = _scheduleChanged.WaitAsync(waitCancellation.Token);
                await Task.WhenAny(timer, changed).ConfigureAwait(false);
                await waitCancellation.CancelAsync().ConfigureAwait(false);
                if (timer.IsCompletedSuccessfully && HasExpiredReset(State.LastQuota, _timeProvider.GetUtcNow()))
                {
                    Publish(State with { NextScheduledFetch = _timeProvider.GetUtcNow() });
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateOnly today = Today();
        EvaluateStaleness();

        Publish(State with
        {
            LastAttempt = now,
            NextScheduledFetch = now + _interval,
            StatusText = State.LastQuota is null ? new AppMessage("Fetching").Format() : State.StatusText,
            StatusMessage = State.LastQuota is null ? new AppMessage("Fetching") : State.StatusMessage,
        });

        try
        {
            ProviderRefreshResult result = await _provider.FetchAsync(today, cancellationToken).ConfigureAwait(false);
            DateTimeOffset completedAt = _timeProvider.GetUtcNow();

            if (_accountKey is not null &&
                result.AccountKey is not null &&
                !string.Equals(_accountKey, result.AccountKey, StringComparison.Ordinal))
            {
                // Account changed: never present the previous account's quota as current.
                Publish(State with
                {
                    LastQuota = null,
                    DailyUsage = null,
                    StatusText = new AppMessage("AccountRefreshing").Format(),
                    StatusMessage = new AppMessage("AccountRefreshing"),
                });
            }

            _accountKey = result.AccountKey ?? _accountKey;
            _consecutiveFailures = 0;

            Publish(State with
            {
                Status = HasExpiredReset(result.Quota, completedAt) ? RefreshStatus.Stale : RefreshStatus.Fresh,
                LastQuota = result.Quota,
                DailyUsage = today == Today() ? result.DailyUsage : DailyUsageResult.Missing(Today()),
                LastSuccess = completedAt,
                NextScheduledFetch = completedAt + _interval,
                StatusText = HasExpiredReset(result.Quota, completedAt) ? new AppMessage("ResetExpired").Format() : null,
                StatusMessage = HasExpiredReset(result.Quota, completedAt) ? new AppMessage("ResetExpired") : null,
                FailureKind = ProviderFailureKind.None,
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Publish(State with
            {
                LastAttempt = now,
                NextScheduledFetch = now + _interval,
                Status = State.LastQuota is null ? RefreshStatus.Error : RefreshStatus.Stale,
                FailureKind = ProviderFailureKind.Canceled,
                StatusText = new AppMessage("RefreshCanceled").Format(),
                StatusMessage = new AppMessage("RefreshCanceled"),
            });
            throw;
        }
        catch (QuotaProviderException ex)
        {
            PublishFailure(ex.Kind, ex.Message, now, ex.PresentationMessage);
        }
        catch (Exception)
        {
            var message = new AppMessage("RefreshFailure");
            PublishFailure(ProviderFailureKind.Unknown, message.Format(), _timeProvider.GetUtcNow(), message);
        }
        finally { WakeSchedule(); }
    }

    private void PublishFailure(ProviderFailureKind kind, string message, DateTimeOffset now, AppMessage? presentationMessage = null)
    {
        if (kind is ProviderFailureKind.NotSignedIn or ProviderFailureKind.UnsupportedAccount or ProviderFailureKind.AccountChanged)
        {
            InvalidateAccount();
        }
        bool isConfigurationProblem = kind is ProviderFailureKind.NotSignedIn
            or ProviderFailureKind.UnsupportedAccount
            or ProviderFailureKind.UnsupportedMethod
            or ProviderFailureKind.CliNotFound
            or ProviderFailureKind.NodeNotFound;

        if (isConfigurationProblem)
        {
            _consecutiveFailures = 0;
        }
        else
        {
            _consecutiveFailures = Math.Min(_consecutiveFailures + 1, RetryDelaysSeconds.Length);
        }

        TimeSpan delay = isConfigurationProblem ? _interval : NextRetryDelay(_consecutiveFailures);

        RefreshStatus status = kind switch
        {
            ProviderFailureKind.NotSignedIn => RefreshStatus.SignedOut,
            ProviderFailureKind.UnsupportedAccount => RefreshStatus.Unsupported,
            ProviderFailureKind.UnsupportedMethod => RefreshStatus.Unsupported,
            ProviderFailureKind.CliNotFound => RefreshStatus.Unsupported,
            ProviderFailureKind.NodeNotFound => RefreshStatus.Unsupported,
            _ => State.LastQuota is null ? RefreshStatus.Error : RefreshStatus.Stale,
        };

        Publish(State with
        {
            Status = status,
            NextScheduledFetch = now + delay,
            StatusText = message,
            StatusMessage = presentationMessage,
            FailureKind = kind,
        });
    }

    private static TimeSpan NextRetryDelay(int consecutiveFailures)
    {
        int index = Math.Clamp(consecutiveFailures - 1, 0, RetryDelaysSeconds.Length - 1);
        return TimeSpan.FromSeconds(RetryDelaysSeconds[index]);
    }

    private DateOnly Today() =>
        DateOnly.FromDateTime(_timeProvider.GetLocalNow().Date);

    private void Publish(RefreshState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    private void InvalidateAccount()
    {
        _accountKey = null;
        Publish(State with { Status = RefreshStatus.Loading, LastQuota = null, LastSuccess = null, DailyUsage = DailyUsageResult.Missing(Today()), StatusText = new AppMessage("AccountChangedWaiting").Format(), StatusMessage = new AppMessage("AccountChangedWaiting") });
    }

    private static bool HasExpiredReset(QuotaSnapshot? quota, DateTimeOffset now) =>
        QuotaProjection.SelectMainBucket(quota)?.Windows.Any(window => window.ResetsAt <= now) == true;

    private void WakeSchedule()
    {
        try { _scheduleChanged.Release(); } catch (SemaphoreFullException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await StopAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        if (_provider is IAccountChangeSource accountChanges) { accountChanges.AccountChanged -= InvalidateAccount; }
        _gate.Dispose();
        _scheduleChanged.Dispose();
        _shutdown.Dispose();
        await _provider.DisposeAsync().ConfigureAwait(false);
    }
}
