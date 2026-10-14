using CodexQuota.Core.Services;

namespace CodexQuota.Tests.Support;

/// <summary>Records scheduled callbacks so tests can fire hover/close delays deterministically.</summary>
public sealed class ManualDelayScheduler : IDelayScheduler
{
    private sealed class Handle : IDisposable
    {
        public Action? Callback;
        public bool Disposed;

        public void Dispose() => Disposed = true;
    }

    private readonly List<Handle> _handles = [];

    public int PendingCount => _handles.Count(handle => !handle.Disposed && handle.Callback is not null);

    public TimeSpan? LastDelay { get; private set; }

    public IDisposable Schedule(TimeSpan delay, Action callback)
    {
        LastDelay = delay;
        var handle = new Handle { Callback = callback };
        _handles.Add(handle);
        return handle;
    }

    /// <summary>Fires every callback that is still pending, mimicking the delay elapsing.</summary>
    public void FireAll()
    {
        foreach (Handle handle in _handles.Where(handle => !handle.Disposed && handle.Callback is not null).ToList())
        {
            Action callback = handle.Callback!;
            handle.Callback = null;
            callback();
        }
    }
}

/// <summary>In-memory start-at-login registration used to verify toggle and cleanup behavior.</summary>
public sealed class FakeStartupRegistration : IStartupRegistration
{
    private string? _command;

    public string ValueName => "CodexQuotaWidget";

    public List<string> Operations { get; } = [];

    /// <summary>When false, <see cref="Enable"/> fails like a denied registry write.</summary>
    public bool AllowEnable { get; set; } = true;

    public string? StoredCommand => _command;

    public bool IsEnabled() => _command is not null;

    public bool Enable(string command)
    {
        Operations.Add("enable:" + command);
        if (!AllowEnable)
        {
            return false;
        }

        _command = command;
        return true;
    }

    public void Disable()
    {
        Operations.Add("disable");
        _command = null;
    }
}
