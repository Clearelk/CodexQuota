using System.Text.Json;

namespace CodexQuota.Core.Transport;

/// <summary>
/// Thin JSONL request/response client for one official app-server helper process.
/// </summary>
public interface IJsonRpcClient : IAsyncDisposable
{
    /// <summary>Raised for server-originated notifications (messages without an id).</summary>
    event Action<JsonElement>? NotificationReceived;

    /// <summary>Sends <c>initialize</c> and then the <c>initialized</c> notification.</summary>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Sends a request and awaits its correlated response. Only methods on the
    /// production allowlist are accepted.
    /// </summary>
    Task<JsonElement> CallAsync(string method, object? parameters, CancellationToken cancellationToken);
}

/// <summary>Creates one client (and therefore one owned helper) per call.</summary>
public interface IJsonRpcClientFactory
{
    IJsonRpcClient Create(CodexLaunchSpec spec);
}
