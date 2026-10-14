using CodexQuota.Core.Services;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using CodexQuota.Core.Providers;

namespace CodexQuota.Core.Transport;

/// <summary>
/// Minimal JSONL JSON-RPC client over an owned official helper process. It owns
/// framing, request correlation, timeouts and process-tree cleanup; it never logs or
/// surfaces raw helper stdout/stderr text.
/// </summary>
public sealed class JsonRpcProcessClient : IJsonRpcClient
{
    /// <summary>Default per-request deadline.</summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Documented maximum frame size so a corrupt helper cannot exhaust memory.</summary>
    public const int DefaultMaxFrameChars = 8 * 1024 * 1024;

    /// <summary>Grace period for a helper to exit after stdin is closed.</summary>
    public static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(3);

    /// <summary>The only methods this release is allowed to emit.</summary>
    public static readonly IReadOnlySet<string> ProductionMethods = new HashSet<string>(StringComparer.Ordinal)
    {
        "initialize",
        "initialized",
        "account/read",
        "account/rateLimits/read",
        "account/usage/read",
    };

    public const string ClientName = "codex_quota_widget";
    public const string ClientTitle = "Codex Quota Widget";
    public const string ClientVersion = "0.1.0";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ITransportProcess _process;
    private readonly TimeSpan _requestTimeout;
    private readonly int _maxFrameChars;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly object _faultGate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _stdoutLoop;
    private readonly Task _stderrLoop;
    private int _nextId;
    private bool _initialized;
    private bool _disposed;
    private Exception? _fault;

    public JsonRpcProcessClient(
        ITransportProcess process,
        TimeSpan? requestTimeout = null,
        int maxFrameChars = DefaultMaxFrameChars)
    {
        _process = process;
        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
        _maxFrameChars = maxFrameChars;
        _stdoutLoop = Task.Run(RunStdoutLoopAsync);
        _stderrLoop = Task.Run(RunStderrLoopAsync);
    }

    public event Action<JsonElement>? NotificationReceived;

    public Task InitializeAsync(CancellationToken cancellationToken) => InitializeCoreAsync(cancellationToken);

    public Task<JsonElement> CallAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        if (!_initialized)
        {
            throw new InvalidOperationException(new AppMessage("InitializeRequired").Format());
        }

        return CallCoreAsync(method, parameters, cancellationToken);
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        var parameters = new Dictionary<string, object?>
        {
            ["clientInfo"] = new Dictionary<string, object?>
            {
                ["name"] = ClientName,
                ["title"] = ClientTitle,
                ["version"] = ClientVersion,
            },
        };

        await CallCoreAsync("initialize", parameters, cancellationToken).ConfigureAwait(false);
        await WritePayloadAsync(BuildPayload(id: null, "initialized", new Dictionary<string, object?>()), cancellationToken)
            .ConfigureAwait(false);
        _initialized = true;
    }

    private async Task<JsonElement> CallCoreAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        EnsureNotFaulted();

        if (!ProductionMethods.Contains(method))
        {
            throw QuotaProviderException.UnsupportedMethod(new AppMessage("UnauthorizedMethod", method));
        }

        int id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_faultGate)
        {
            EnsureNotFaulted();
            _pending[id] = completion;
        }

        try
        {
            using var timeout = new CancellationTokenSource(_requestTimeout);
            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token, _lifetime.Token);

            try
            {
                await WritePayloadAsync(BuildPayload(id, method, parameters), linked.Token).ConfigureAwait(false);
                return await completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                QuotaProviderException error = QuotaProviderException.Timeout(
                    new AppMessage("RequestTimeout", method, _requestTimeout.TotalSeconds));
                Fault(error);
                throw error;
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task WritePayloadAsync(string payload, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureNotFaulted();
            await _process.StandardInput.WriteLineAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not QuotaProviderException)
        {
            QuotaProviderException error = QuotaProviderException.Transport(new AppMessage("RequestWriteFailure"), ex);
            Fault(error);
            throw error;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static string BuildPayload(int? id, string method, object? parameters)
    {
        var payload = new Dictionary<string, object?>(3);
        if (id is { } requestId)
        {
            payload["id"] = requestId;
        }

        payload["method"] = method;
        if (parameters is not null)
        {
            payload["params"] = parameters;
        }

        return JsonSerializer.Serialize(payload, SerializerOptions);
    }

    private async Task RunStdoutLoopAsync()
    {
        try
        {
            var buffer = new char[4096];
            var frame = new StringBuilder();
            while (!_lifetime.IsCancellationRequested)
            {
                int count = await _process.StandardOutput.ReadAsync(buffer.AsMemory(), _lifetime.Token).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }
                for (int index = 0; index < count; index++)
                {
                    char character = buffer[index];
                    if (character == '\n')
                    {
                        if (frame.Length > 0)
                        {
                            Dispatch(frame.ToString().TrimEnd('\r'));
                            frame.Clear();
                        }
                    }
                    else
                    {
                        if (frame.Length >= _maxFrameChars)
                        {
                            Fault(QuotaProviderException.Transport(new AppMessage("FrameTooLong", _maxFrameChars)));
                            return;
                        }
                        frame.Append(character);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Fault(QuotaProviderException.Transport(new AppMessage("HelperReadFailure"), ex));
            return;
        }

        if (!_lifetime.IsCancellationRequested)
        {
            Fault(QuotaProviderException.ProcessExited(new AppMessage("HelperExited")));
        }
    }

    private async Task RunStderrLoopAsync()
    {
        try
        {
            var buffer = new char[4096];
            while (await _process.StandardError.ReadAsync(buffer.AsMemory(), _lifetime.Token).ConfigureAwait(false) > 0)
            {
                // Deliberately drained and discarded: raw helper stderr is never shown or persisted.
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // A stderr read failure is not itself a protocol failure.
        }
    }

    private void Dispatch(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            Fault(QuotaProviderException.Transport(new AppMessage("HelperInvalidMessage")));
            return;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            bool hasId = TryReadId(root, out int id);
            bool hasMethod = root.TryGetProperty("method", out JsonElement methodElement) &&
                methodElement.ValueKind == JsonValueKind.String;

            if (hasId && hasMethod)
            {
                RespondUnsupported(id);
                return;
            }

            if (hasId)
            {
                if (!_pending.TryRemove(id, out TaskCompletionSource<JsonElement>? completion))
                {
                    return;
                }

                if (root.TryGetProperty("error", out JsonElement error))
                {
                    completion.TrySetException(MapError(error));
                }
                else if (root.TryGetProperty("result", out JsonElement result))
                {
                    completion.TrySetResult(result.Clone());
                }
                else
                {
                    completion.TrySetResult(root.Clone());
                }

                return;
            }

            if (hasMethod)
            {
                NotificationReceived?.Invoke(root.Clone());
            }
        }
    }

    private void RespondUnsupported(int id)
    {
        var payload = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["error"] = new Dictionary<string, object?>
            {
                ["code"] = -32601,
                ["message"] = "unsupported",
            },
        };

        _ = SendBestEffortAsync(JsonSerializer.Serialize(payload, SerializerOptions));
    }

    private async Task SendBestEffortAsync(string payload)
    {
        try
        {
            await WritePayloadAsync(payload, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Responding to an unexpected server request is best-effort only.
        }
    }

    private static bool TryReadId(JsonElement root, out int id)
    {
        id = 0;
        if (!root.TryGetProperty("id", out JsonElement element))
        {
            return false;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt32(out id),
            JsonValueKind.String => int.TryParse(element.GetString(), out id),
            _ => false,
        };
    }

    private static QuotaProviderException MapError(JsonElement error)
    {
        int code = 0;
        if (error.ValueKind == JsonValueKind.Object &&
            error.TryGetProperty("code", out JsonElement codeElement) &&
            codeElement.ValueKind == JsonValueKind.Number)
        {
            codeElement.TryGetInt32(out code);
        }

        return code switch
        {
            -32601 => QuotaProviderException.UnsupportedMethod(new AppMessage("MethodNotFound")),
            -32602 => QuotaProviderException.InvalidData(new AppMessage("InvalidParameters")),
            _ => QuotaProviderException.Transport(new AppMessage("ApiError", code)),
        };
    }

    private void EnsureNotFaulted()
    {
        Exception? fault = _fault;
        if (fault is null)
        {
            return;
        }

        if (fault is QuotaProviderException providerException)
        {
            throw providerException;
        }

        throw QuotaProviderException.Transport(fault.Message, fault);
    }

    private void Fault(Exception exception)
    {
        List<TaskCompletionSource<JsonElement>> pending;
        lock (_faultGate)
        {
            _fault ??= exception;
            pending = _pending.Values.ToList();
            _pending.Clear();
        }

        foreach (TaskCompletionSource<JsonElement> completion in pending)
        {
            completion.TrySetException(exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _process.StandardInput.Close();
        }
        catch
        {
            // Closing stdin is best-effort; the tree is killed below if the helper lingers.
        }

        using (var grace = new CancellationTokenSource(ShutdownGrace))
        {
            try
            {
                await _process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    _process.KillEntireTree();
                }
                catch
                {
                    // The helper may already be gone.
                }
            }
        }

        // The parent can exit normally while descendants still own inherited pipe handles.
        // Windows job ownership terminates those descendants even after the parent exits.
        try { _process.KillEntireTree(); } catch (InvalidOperationException) { }
        _lifetime.Cancel();
        Fault(QuotaProviderException.ProcessExited(new AppMessage("RefreshEnded")));

        try
        {
            await Task.WhenAll(_stdoutLoop, _stderrLoop).ConfigureAwait(false);
        }
        catch
        {
            // Loop faults are surfaced through pending requests, not here.
        }

        await _process.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
        _writeGate.Dispose();
    }
}

/// <summary>Default factory: starts a real helper process, one client per helper.</summary>
public sealed class JsonRpcProcessClientFactory : IJsonRpcClientFactory
{
    private readonly ITransportProcessFactory _processFactory;
    private readonly TimeSpan? _requestTimeout;
    private readonly int _maxFrameChars;

    public JsonRpcProcessClientFactory(
        ITransportProcessFactory processFactory,
        TimeSpan? requestTimeout = null,
        int maxFrameChars = JsonRpcProcessClient.DefaultMaxFrameChars)
    {
        _processFactory = processFactory;
        _requestTimeout = requestTimeout;
        _maxFrameChars = maxFrameChars;
    }

    public IJsonRpcClient Create(CodexLaunchSpec spec) =>
        new JsonRpcProcessClient(_processFactory.Start(spec), _requestTimeout, _maxFrameChars);
}
