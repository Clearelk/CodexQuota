using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace CodexQuota.Desktop.Services;

/// <summary>Observes button-down positions only while a panel is open. Never consumes input.</summary>
internal sealed class OutsideClickObserver : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly HookProcedure _procedure;
    private nint _hook;
    private int _generation;

    public OutsideClickObserver(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _procedure = OnMouse;
    }

    public bool IsRunning => _hook != 0;
    public event Action<Point, nint, bool>? ButtonDown;

    public void Start()
    {
        if (IsRunning) { return; }
        _generation++;
        _hook = SetWindowsHookEx(14, _procedure, GetModuleHandle(null), 0);
        if (_hook == 0) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
    }

    public void Stop()
    {
        _generation++;
        if (_hook != 0) { UnhookWindowsHookEx(_hook); _hook = 0; }
    }

    private nint OnMouse(int code, nint message, nint data)
    {
        if (code >= 0 && message is 0x0201 or 0x0204 or 0x0207 or 0x020B)
        {
            NativePoint point = Marshal.PtrToStructure<NativePoint>(data);
            ObserveButtonDown(new Point(point.X, point.Y));
        }
        return CallNextHookEx(_hook, code, message, data);
    }

    // Also used by native-window verification to drive the production callback boundary
    // without moving the user's pointer or claiming physical input coverage.
    internal void ObserveButtonDown(Point point)
    {
        if (!IsRunning) { return; }
        nint target = WindowFromPoint(new NativePoint { X = (int)point.X, Y = (int)point.Y });
        GetWindowThreadProcessId(target, out uint owner);
        bool owned = owner == Environment.ProcessId;
        int generation = _generation;
        _dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (IsRunning && generation == _generation) { ButtonDown?.Invoke(point, target, owned); }
        });
    }

    public void Dispose() => Stop();

    private delegate nint HookProcedure(int code, nint message, nint data);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int id, HookProcedure procedure, nint module, uint thread);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint process);
}
