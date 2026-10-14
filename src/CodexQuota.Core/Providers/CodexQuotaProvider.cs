using System.Text.Json;
using CodexQuota.Core.Models;
using CodexQuota.Core.Services;
using CodexQuota.Core.Transport;

namespace CodexQuota.Core.Providers;

/// <summary>
/// The only provider shipped in this release. One <see cref="FetchAsync"/> call owns
/// exactly one short-lived official helper: it initializes it, reads the account,
/// quota and the optional daily statistic through that same process, and disposes the
/// whole owned tree before returning on success, failure or cancellation.
/// </summary>
public sealed class CodexQuotaProvider : IQuotaProvider, IAccountChangeSource
{
    private static readonly Dictionary<string, object?> EmptyParameters = [];

    private readonly IJsonRpcClientFactory _clientFactory;
    private readonly Func<CodexLaunchSpec> _launchSpecFactory;
    private readonly TimeProvider _timeProvider;
    private readonly bool _includeDailyUsage;
    private string? _accountKey;
    public event Action? AccountChanged;

    public CodexQuotaProvider(
        IJsonRpcClientFactory clientFactory,
        CodexLaunchSpec launchSpec,
        TimeProvider? timeProvider = null,
        bool includeDailyUsage = false)
        : this(clientFactory, () => launchSpec, timeProvider, includeDailyUsage)
    {
    }

    /// <summary>Creates the official provider that owns one short-lived helper per fetch.</summary>
    /// <param name="clientFactory">Factory for the JSON-RPC transport over the official helper.</param>
    /// <param name="launchSpecFactory">Resolves the official Codex launch specification per fetch.</param>
    /// <param name="timeProvider">Clock used for timestamps; defaults to the system clock.</param>
    /// <param name="includeDailyUsage">
    /// Opt-in legacy <c>account/usage/read</c> support. It defaults to off because daily
    /// activity is now derived from local Codex records; the normal desktop path never
    /// requests the optional official daily method. Decoder and provider coverage remain
    /// available for compatibility and tests.
    /// </param>
    public CodexQuotaProvider(
        IJsonRpcClientFactory clientFactory,
        Func<CodexLaunchSpec> launchSpecFactory,
        TimeProvider? timeProvider = null,
        bool includeDailyUsage = false)
    {
        _clientFactory = clientFactory;
        _launchSpecFactory = launchSpecFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _includeDailyUsage = includeDailyUsage;
    }

    public string Id => "codex";

    public async Task<ProviderRefreshResult> FetchAsync(DateOnly date, CancellationToken cancellationToken)
    {
        await using IJsonRpcClient client = _clientFactory.Create(_launchSpecFactory());
        await client.InitializeAsync(cancellationToken).ConfigureAwait(false);
        int accountUpdated = 0;
        JsonElement? updatedQuota = null;
        object notificationGate = new();
        client.NotificationReceived += notification =>
        {
            if (!notification.TryGetProperty("method", out JsonElement method)) { return; }
            if (method.GetString() == "account/updated")
            {
                Interlocked.Exchange(ref accountUpdated, 1);
                AccountChanged?.Invoke();
            }
            else if (method.GetString() == "account/rateLimits/updated" && notification.TryGetProperty("params", out JsonElement parameters))
            {
                lock (notificationGate) { updatedQuota = parameters.Clone(); }
            }
        };

        JsonElement account = await client.CallAsync("account/read", EmptyParameters, cancellationToken).ConfigureAwait(false);
        string? accountType = CodexResponseDecoder.DecodeAccountType(account);
        if (accountType is null)
        {
            throw QuotaProviderException.NotSignedIn(
                new AppMessage("ProviderSignedOut"));
        }

        if (!string.Equals(accountType, "chatgpt", StringComparison.OrdinalIgnoreCase))
        {
            throw QuotaProviderException.UnsupportedAccount(
                new AppMessage("ProviderAccountUnsupported", accountType));
        }

        string? accountKey = CodexResponseDecoder.DecodeAccountKey(account);
        if (_accountKey is not null && !string.Equals(_accountKey, accountKey, StringComparison.Ordinal))
        {
            AccountChanged?.Invoke();
        }
        _accountKey = accountKey;

        JsonElement rateLimits = await client.CallAsync("account/rateLimits/read", EmptyParameters, cancellationToken).ConfigureAwait(false);
        QuotaSnapshot quota = CodexResponseDecoder.DecodeQuota(rateLimits, _timeProvider.GetUtcNow(), CodexResponseDecoder.DecodeAccountPlan(account));

        QuotaBucket? mainBucket = QuotaProjection.SelectMainBucket(quota);
        if (mainBucket is null)
        {
            throw QuotaProviderException.UnsupportedMethod(
                new AppMessage("ProviderBucketMissing"));
        }

        if (mainBucket.ContainsInvalidWindow)
        {
            throw QuotaProviderException.InvalidData(
                new AppMessage("ProviderInvalidMain"));
        }

        DailyUsageResult daily = _includeDailyUsage
            ? await FetchDailyAsync(client, date, cancellationToken).ConfigureAwait(false)
            : DailyUsageResult.Missing(date);
        // Drain/close the entire owned session before accepting its final account epoch.
        await client.DisposeAsync().ConfigureAwait(false);
        if (Volatile.Read(ref accountUpdated) != 0)
        {
            throw new QuotaProviderException(ProviderFailureKind.AccountChanged, new AppMessage("ProviderAccountChanged"));
        }
        lock (notificationGate)
        {
            if (updatedQuota is { } update)
            {
                QuotaSnapshot candidate = CodexResponseDecoder.DecodeQuota(update, _timeProvider.GetUtcNow(), quota.Plan);
                QuotaBucket? updatedMain = QuotaProjection.SelectMainBucket(candidate);
                if (updatedMain?.ContainsInvalidWindow == true) { throw QuotaProviderException.InvalidData(new AppMessage("ProviderInvalidUpdate")); }
                if (updatedMain is not null) { quota = candidate; }
            }
        }
        return new ProviderRefreshResult(quota, daily, accountKey);
    }

    private static async Task<DailyUsageResult> FetchDailyAsync(
        IJsonRpcClient client,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        try
        {
            JsonElement usage = await client.CallAsync("account/usage/read", EmptyParameters, cancellationToken).ConfigureAwait(false);
            return CodexResponseDecoder.DecodeDailyUsage(usage, date);
        }
        catch (QuotaProviderException ex) when (ex.Kind == ProviderFailureKind.UnsupportedMethod)
        {
            return DailyUsageResult.Unsupported(date, new AppMessage("OfficialDailyUnsupported"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (QuotaProviderException ex)
        {
            return DailyUsageResult.Failed(date, ex.Message) with { ExplanationMessage = ex.PresentationMessage };
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
