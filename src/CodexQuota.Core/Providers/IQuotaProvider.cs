using CodexQuota.Core.Models;

namespace CodexQuota.Core.Providers;

/// <summary>
/// Platform-neutral quota source. A single <see cref="FetchAsync"/> call owns one
/// short-lived official refresh session and disposes it before returning. Only the
/// Codex implementation is registered in this release; the interface exists so a
/// future Claude/Gemini provider can be added without touching quota interpretation
/// or presentation.
/// </summary>
public interface IQuotaProvider : IAsyncDisposable
{
    /// <summary>Stable provider identifier, for example <c>codex</c>.</summary>
    string Id { get; }

    /// <summary>
    /// Fetches quota and, when available, the optional daily Token statistic for the
    /// supplied calendar date. Throws <see cref="QuotaProviderException"/> for quota
    /// failures; an optional daily failure is reported inside the result instead.
    /// </summary>
    Task<ProviderRefreshResult> FetchAsync(DateOnly date, CancellationToken cancellationToken);
}
