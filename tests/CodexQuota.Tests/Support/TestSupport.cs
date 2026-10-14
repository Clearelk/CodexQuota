using System.Diagnostics;
using CodexQuota.Core.Models;
using CodexQuota.Core.Providers;
using CodexQuota.Core.Transport;

namespace CodexQuota.Tests.Support;

/// <summary>Deterministic clock; the local timezone is fixed to UTC so "today" is stable.</summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public FakeTimeProvider(DateTimeOffset start) => _utcNow = start;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan delta) => _utcNow += delta;

    public void Set(DateTimeOffset value) => _utcNow = value;
}

/// <summary>Configurable provider double used to exercise the coordinator deterministically.</summary>
public sealed class FakeQuotaProvider : IQuotaProvider
{
    private readonly QuotaSnapshot? _quota;
    private readonly DailyUsageResult? _daily;
    private readonly Exception? _exception;
    private readonly string? _accountKey;

    public FakeQuotaProvider(
        QuotaSnapshot? quota = null,
        DailyUsageResult? daily = null,
        Exception? exception = null,
        string? accountKey = null)
    {
        _quota = quota;
        _daily = daily;
        _exception = exception;
        _accountKey = accountKey;
    }

    public string Id => "fake";

    public int CallCount { get; private set; }

    public List<DateOnly> RequestedDates { get; } = [];

    public bool Disposed { get; private set; }

    /// <summary>Optional hook that runs inside a fetch, for overlap/timing tests.</summary>
    public Func<DateOnly, CancellationToken, Task>? OnFetch { get; init; }

    public async Task<ProviderRefreshResult> FetchAsync(DateOnly date, CancellationToken cancellationToken)
    {
        CallCount++;
        RequestedDates.Add(date);

        if (OnFetch is not null)
        {
            await OnFetch(date, cancellationToken).ConfigureAwait(false);
        }

        if (_exception is not null)
        {
            throw _exception;
        }

        QuotaSnapshot quota = _quota ?? Fixtures.MainQuota(54);
        DailyUsageResult daily = _daily ?? DailyUsageResult.Missing(date);
        return new ProviderRefreshResult(quota, daily, _accountKey);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Synthetic snapshots for tests. No live account data is stored here.</summary>
public static class Fixtures
{
    public static readonly DateTimeOffset FetchedAt = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    public static QuotaSnapshot MainQuota(int usedPercent, int? weeklyUsedPercent = null)
    {
        var windows = new List<QuotaWindow>
        {
            new("primary", 300, usedPercent, 100 - usedPercent, FetchedAt.AddMinutes(5)),
        };

        if (weeklyUsedPercent is { } weekly)
        {
            windows.Add(new QuotaWindow("secondary", 10080, weekly, 100 - weekly, FetchedAt.AddDays(1)));
        }

        var bucket = new QuotaBucket("codex", null, windows, ContainsInvalidWindow: false, "0", false, false, null)
        {
            PlanType = "plus",
        };

        return new QuotaSnapshot("codex", "plus", [bucket], FetchedAt);
    }
}

/// <summary>Implements the executable-discovery seam with an in-memory file table.</summary>
public sealed class FakeExecutableEnvironment : IExecutableEnvironment
{
    public Dictionary<string, string?> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> SearchDirectories { get; } = [];

    public HashSet<string> ExistingFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, List<string>> SubDirectories { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsWindows { get; set; } = true;

    public string? GetEnvironmentVariable(string name) =>
        Variables.TryGetValue(name, out string? value) ? value : null;

    public IReadOnlyList<string> GetSearchDirectories() => SearchDirectories;

    public bool FileExists(string path) => ExistingFiles.Contains(path);

    public IReadOnlyList<string> GetSubDirectories(string directory) =>
        SubDirectories.TryGetValue(directory, out List<string>? children) ? children : [];

    public void AddFile(string path) => ExistingFiles.Add(path);
}

/// <summary>Launches the synthetic JSONL helper for protocol tests.</summary>
public static class FakeServerHarness
{
    public static string ExecutablePath { get; } =
        Path.Combine(AppContext.BaseDirectory, "CodexQuota.FakeServer.exe");

    public static bool IsAvailable => File.Exists(ExecutablePath);

    public static CodexLaunchSpec Spec(string scenario, string? pidFile = null, string? childPidFile = null, string? appendFile = null)
    {
        var arguments = new List<string> { "--scenario", scenario };

        if (pidFile is not null)
        {
            arguments.Add("--pidfile");
            arguments.Add(pidFile);
        }

        if (childPidFile is not null)
        {
            arguments.Add("--childpidfile");
            arguments.Add(childPidFile);
        }

        if (appendFile is not null)
        {
            arguments.Add("--append");
            arguments.Add(appendFile);
        }

        if (IsAvailable)
        {
            return new CodexLaunchSpec(ExecutablePath, arguments, ExecutablePath);
        }

        string dll = Path.Combine(AppContext.BaseDirectory, "CodexQuota.FakeServer.dll");
        var dotnetArguments = new List<string> { dll };
        dotnetArguments.AddRange(arguments);
        return new CodexLaunchSpec("dotnet", dotnetArguments, dll);
    }

    public static bool ProcessExists(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
