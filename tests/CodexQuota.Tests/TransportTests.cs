using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CodexQuota.Core.Models;
using CodexQuota.Core.Providers;
using CodexQuota.Core.Transport;
using CodexQuota.Tests.Support;

namespace CodexQuota.Tests;

public sealed class TransportTests
{
    private static JsonRpcProcessClient CreateClient(
        string scenario,
        TimeSpan? timeout = null,
        int maxFrameChars = JsonRpcProcessClient.DefaultMaxFrameChars,
        string? appendFile = null,
        string? childPidFile = null)
    {
        var factory = new ProcessTransportProcessFactory();
        ITransportProcess process = factory.Start(FakeServerHarness.Spec(scenario, appendFile: appendFile, childPidFile: childPidFile));
        return new JsonRpcProcessClient(process, timeout, maxFrameChars);
    }

    [Fact]
    public async Task HandshakeThenAccountReadSucceeds()
    {
        await using JsonRpcProcessClient client = CreateClient("happy");
        await client.InitializeAsync(CancellationToken.None);

        JsonElement account = await client.CallAsync("account/read", new Dictionary<string, object?>(), CancellationToken.None);

        Assert.Equal("chatgpt", CodexResponseDecoder.DecodeAccountType(account));
    }

    [Fact]
    public async Task InterleavedNotificationsDoNotCompletePendingRequests()
    {
        await using JsonRpcProcessClient client = CreateClient("notifications");
        await client.InitializeAsync(CancellationToken.None);

        JsonElement account = await client.CallAsync("account/read", new Dictionary<string, object?>(), CancellationToken.None);

        Assert.Equal("chatgpt", CodexResponseDecoder.DecodeAccountType(account));
    }

    [Fact]
    public async Task ServerOriginatedRequestIsAnsweredWithoutBlockingLaterReads()
    {
        await using JsonRpcProcessClient client = CreateClient("server-request");
        await client.InitializeAsync(CancellationToken.None);

        JsonElement account = await client.CallAsync("account/read", new Dictionary<string, object?>(), CancellationToken.None);

        Assert.Equal("chatgpt", CodexResponseDecoder.DecodeAccountType(account));
    }

    [Fact]
    public async Task MethodNotFoundMapsToUnsupportedMethod()
    {
        await using JsonRpcProcessClient client = CreateClient("usage-method-not-found");
        await client.InitializeAsync(CancellationToken.None);

        QuotaProviderException error = await Assert.ThrowsAsync<QuotaProviderException>(
            () => client.CallAsync("account/usage/read", new Dictionary<string, object?>(), CancellationToken.None));

        Assert.Equal(ProviderFailureKind.UnsupportedMethod, error.Kind);
    }

    [Fact]
    public async Task MethodsOutsideTheProductionAllowlistAreRejectedWithoutTouchingTheHelper()
    {
        await using JsonRpcProcessClient client = CreateClient("happy");
        await client.InitializeAsync(CancellationToken.None);

        QuotaProviderException error = await Assert.ThrowsAsync<QuotaProviderException>(
            () => client.CallAsync("thread/start", new Dictionary<string, object?>(), CancellationToken.None));

        Assert.Equal(ProviderFailureKind.UnsupportedMethod, error.Kind);
    }

    [Fact]
    public async Task CallingBeforeInitializeIsRejected()
    {
        await using JsonRpcProcessClient client = CreateClient("happy");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.CallAsync("account/read", new Dictionary<string, object?>(), CancellationToken.None));
    }

    [Fact]
    public async Task HangingRequestTimesOutAndFaultsTheSession()
    {
        await using JsonRpcProcessClient client = CreateClient("hang", timeout: TimeSpan.FromMilliseconds(600));
        await client.InitializeAsync(CancellationToken.None);

        QuotaProviderException error = await Assert.ThrowsAsync<QuotaProviderException>(
            () => client.CallAsync("account/read", new Dictionary<string, object?>(), CancellationToken.None));
        Assert.Equal(ProviderFailureKind.Timeout, error.Kind);

        // After a timeout the session must not be reused.
        await Assert.ThrowsAsync<QuotaProviderException>(
            () => client.CallAsync("account/read", new Dictionary<string, object?>(), CancellationToken.None));
    }

    [Fact]
    public async Task InitializeTimeoutIsReportedAsTimeout()
    {
        await using JsonRpcProcessClient client = CreateClient("hang-all", timeout: TimeSpan.FromMilliseconds(500));

        QuotaProviderException error = await Assert.ThrowsAsync<QuotaProviderException>(
            () => client.InitializeAsync(CancellationToken.None));

        Assert.Equal(ProviderFailureKind.Timeout, error.Kind);
    }

    [Fact]
    public async Task HelperExitFailsPendingRequests()
    {
        await using JsonRpcProcessClient client = CreateClient("exit-after-init");
        await client.InitializeAsync(CancellationToken.None);

        QuotaProviderException error = await Assert.ThrowsAsync<QuotaProviderException>(
            () => client.CallAsync("account/read", new Dictionary<string, object?>(), CancellationToken.None));

        Assert.Contains(
            error.Kind,
            new[] { ProviderFailureKind.ProcessExited, ProviderFailureKind.Transport });
    }

    [Fact]
    public async Task HelperExitDuringInitializeFailsTheHandshake()
    {
        await using var client = CreateClient("exit-during-init");
        QuotaProviderException error = await Assert.ThrowsAsync<QuotaProviderException>(() => client.InitializeAsync(CancellationToken.None));
        Assert.Contains(error.Kind, new[] { ProviderFailureKind.ProcessExited, ProviderFailureKind.Transport });
    }

    [Fact]
    public async Task MalformedFrameFaultsTheSession()
    {
        await using JsonRpcProcessClient client = CreateClient("malformed");
        await client.InitializeAsync(CancellationToken.None);

        QuotaProviderException error = await Assert.ThrowsAsync<QuotaProviderException>(
            () => client.CallAsync("account/read", new Dictionary<string, object?>(), CancellationToken.None));

        Assert.Equal(ProviderFailureKind.Transport, error.Kind);
    }

    [Fact]
    public async Task MalformedInitializeFrameFaultsTheHandshake()
    {
        await using var client = CreateClient("malformed-initialize");
        QuotaProviderException error = await Assert.ThrowsAsync<QuotaProviderException>(() => client.InitializeAsync(CancellationToken.None));
        Assert.Equal(ProviderFailureKind.Transport, error.Kind);
    }

    [Fact]
    public async Task OversizedFrameIsRejected()
    {
        await using JsonRpcProcessClient client = CreateClient("oversized", maxFrameChars: 4096);
        await client.InitializeAsync(CancellationToken.None);

        QuotaProviderException error = await Assert.ThrowsAsync<QuotaProviderException>(
            () => client.CallAsync("account/read", new Dictionary<string, object?>(), CancellationToken.None));

        Assert.Equal(ProviderFailureKind.Transport, error.Kind);
    }

    [Fact]
    public async Task DisposeKillsTheWholeOwnedProcessTree()
    {
        string childPidFile = Path.Combine(Path.GetTempPath(), $"codexquota-child-{Guid.NewGuid():N}.txt");

        var client = CreateClient("spawn-child", childPidFile: childPidFile);
        await client.InitializeAsync(CancellationToken.None);
        await client.CallAsync("account/read", new Dictionary<string, object?>(), CancellationToken.None);

        int childPid = await WaitForPidAsync(childPidFile);
        Assert.True(FakeServerHarness.ProcessExists(childPid), "the spawned child should be running before disposal");

        await client.DisposeAsync();

        await WaitUntilAsync(() => !FakeServerHarness.ProcessExists(childPid), TimeSpan.FromSeconds(10));
        Assert.False(FakeServerHarness.ProcessExists(childPid), "disposal must kill the owned child process tree");
    }

    [Fact]
    public async Task DisposeIsIdempotent()
    {
        var client = CreateClient("happy");
        await client.InitializeAsync(CancellationToken.None);

        await client.DisposeAsync();
        await client.DisposeAsync();
    }

    [Fact]
    public async Task GracefulParentExitStillKillsOwnedChild()
    {
        string path = Path.Combine(Path.GetTempPath(), $"codexquota-graceful-{Guid.NewGuid():N}.txt");
        var client = CreateClient("graceful-child", childPidFile: path);
        await client.InitializeAsync(CancellationToken.None);
        int childPid = await WaitForPidAsync(path);
        try
        {
            await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(7));
            await WaitUntilAsync(() => !FakeServerHarness.ProcessExists(childPid), TimeSpan.FromSeconds(2));
            Assert.False(FakeServerHarness.ProcessExists(childPid));
        }
        finally
        {
            if (FakeServerHarness.ProcessExists(childPid))
            {
                using Process child = Process.GetProcessById(childPid);
                child.Kill(entireProcessTree: true);
            }
        }
    }

    [Fact]
    public async Task OversizedUnterminatedFrameFailsBeforeDeadline()
    {
        await using var client = CreateClient("oversized-unterminated", TimeSpan.FromSeconds(2), 4096);
        await client.InitializeAsync(CancellationToken.None);
        QuotaProviderException error = await Assert.ThrowsAsync<QuotaProviderException>(
            () => client.CallAsync("account/read", null, CancellationToken.None));
        Assert.Equal(ProviderFailureKind.Transport, error.Kind);
    }

    private static async Task<int> WaitForPidAsync(string path)
    {
        await WaitUntilAsync(() => File.Exists(path) && ReadPid(path) > 0, TimeSpan.FromSeconds(10));
        return ReadPid(path);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }
    }

    private static int ReadPid(string path)
    {
        try
        {
            return int.Parse(File.ReadAllText(path).Trim(), CultureInfo.InvariantCulture);
        }
        catch (IOException)
        {
            return 0;
        }
        catch (FormatException)
        {
            return 0;
        }
    }
}
