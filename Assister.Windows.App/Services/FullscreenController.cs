using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Assister.Windows.App.Services;

internal sealed class FullscreenController(Window window)
{
    private WindowStyle _style;
    private ResizeMode _resizeMode;
    private WindowState _state;
    private Rect _bounds;
    private bool _topmost;
    private double _minWidth;
    private double _minHeight;
    public bool IsFullscreen { get; private set; }

    public void Toggle()
    {
        if (IsFullscreen)
        {
            window.Topmost = _topmost;
            window.WindowState = WindowState.Normal;
            window.WindowStyle = _style;
            window.ResizeMode = _resizeMode;
            window.MinWidth = _minWidth; window.MinHeight = _minHeight;
            window.Left = _bounds.Left; window.Top = _bounds.Top;
            window.Width = _bounds.Width; window.Height = _bounds.Height;
            window.WindowState = _state;
            IsFullscreen = false;
            return;
        }
        var handle = new WindowInteropHelper(window).EnsureHandle();
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref monitor))
            throw new InvalidOperationException("Cannot determine the fullscreen monitor.");
        _style = window.WindowStyle; _resizeMode = window.ResizeMode;
        _state = window.WindowState; _topmost = window.Topmost;
        _minWidth = window.MinWidth; _minHeight = window.MinHeight;
        _bounds = _state == WindowState.Normal ? new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight) : window.RestoreBounds;
        window.WindowState = WindowState.Normal;
        window.WindowStyle = WindowStyle.None;
        window.ResizeMode = ResizeMode.NoResize;
        window.MinWidth = 0; window.MinHeight = 0;
        window.Topmost = true;
        IsFullscreen = true;
        // Native pixel bounds cover the monitor, including its taskbar, at any DPI.
        var rect = monitor.Monitor;
        if (!SetWindowPos(handle, new IntPtr(-1), rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, 0x0020 | 0x0040))
        {
            Toggle();
            throw new InvalidOperationException("Cannot enter fullscreen.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
