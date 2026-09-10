using System.Runtime.InteropServices;

namespace AiUsageBar.Services;

/// <summary>The small bit of Win32 still needed under WinUI: the cursor position
/// (to anchor the popup near the tray click) and the window DPI (to convert
/// effective pixels to physical pixels for AppWindow placement).</summary>
internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(nint hwnd);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;

        /// <summary>The monitor's full rectangle, in virtual-screen pixels.</summary>
        public RECT rcMonitor;

        /// <summary>The same rectangle minus the taskbar and any other appbar.
        /// Comparing the two is how the taskbar's docked edge is derived.</summary>
        public RECT rcWork;

        public uint dwFlags;
    }

    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    public static extern nint MonitorFromPoint(POINT point, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);

    /// <summary>Per-monitor DPI. The process is PerMonitorV2, so the scale of
    /// the monitor holding the tray icon is not necessarily the scale of the
    /// monitor the popup currently sits on.</summary>
    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>MDT_EFFECTIVE_DPI, the scale factor the user actually set.</summary>
    public const int MDT_EFFECTIVE_DPI = 0;
}
