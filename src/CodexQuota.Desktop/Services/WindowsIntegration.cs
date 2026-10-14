using Microsoft.Win32;
using CodexQuota.Core.Services;

namespace CodexQuota.Desktop.Services;

/// <summary>
/// Per-user start-at-login registration under
/// <c>HKCU/Software/Microsoft/Windows/CurrentVersion/Run</c>. Only the widget-owned
/// value is written or removed; the registration itself is always the source of truth.
/// </summary>
public sealed class StartupService : IStartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string ValueName => "CodexQuotaWidget";

    public bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch (Exception)
        {
            throw new InvalidOperationException("无法读取开机启动注册状态。");
        }
    }

    /// <summary>Returns the exact registered command so settings can show the real value.</summary>
    public string? GetRegisteredCommand()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) as string;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public bool Enable(string command)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            key.SetValue(ValueName, command, RegistryValueKind.String);
            return string.Equals(GetRegisteredCommand(), command, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Disable()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception)
        {
            // Disabling must never crash the app; the checkbox reflects the real value.
        }
    }
}

/// <summary>Named per-user-session single-instance guard.</summary>
public sealed class SingleInstanceService : IDisposable
{
    private static string MutexName
    {
        get
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return @"Local\CodexQuota.Widget.SingleInstance." + identity.User!.Value;
        }
    }
    private Mutex? _mutex;

    public bool TryAcquire()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: false, MutexName, out _);
            return _mutex.WaitOne(TimeSpan.Zero, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    public void Dispose()
    {
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch (Exception)
        {
            // The mutex may not be owned if acquisition failed.
        }

        _mutex?.Dispose();
        _mutex = null;
    }
}
