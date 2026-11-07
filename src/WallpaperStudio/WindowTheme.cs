using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WallpaperStudio;

public static class WindowTheme
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int WmGetMinMaxInfo = 0x0024;
    private const int MonitorDefaultToNull = 0;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointNative
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public PointNative Reserved;
        public PointNative MaxSize;
        public PointNative MaxPosition;
        public PointNative MinTrackSize;
        public PointNative MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public RectNative Monitor;
        public RectNative Work;
        public int Flags;
    }

    /// <summary>Windows 10 2004+ / 11 only; older builds keep the light caption.</summary>
    public static void ApplyDarkTitleBar(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        int on = 1;
        _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref on, sizeof(int));
    }

    /// <summary>
    /// 自绘标题栏后，系统默认会把最大化窗口铺满整个显示器并遮住任务栏；
    /// 这里按工作区重算最大化尺寸。
    /// </summary>
    public static void EnableCustomChrome(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        ApplyDarkTitleBar(window);

        HwndSource? source = HwndSource.FromHwnd(handle);
        if (source is null)
        {
            return;
        }

        int minTrackX = (int)window.MinWidth;
        int minTrackY = (int)window.MinHeight;

        source.AddHook(Hook);
        return;

        IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmGetMinMaxInfo)
            {
                MinMaxInfo info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
                MonitorInfo monitor = default;
                monitor.Size = Marshal.SizeOf<MonitorInfo>();

                IntPtr hMonitor = MonitorFromWindow(hwnd, MonitorDefaultToNull);
                if (hMonitor != IntPtr.Zero && GetMonitorInfo(hMonitor, ref monitor))
                {
                    info.MaxSize = new PointNative { X = monitor.Work.Right - monitor.Work.Left, Y = monitor.Work.Bottom - monitor.Work.Top };
                    info.MaxPosition = new PointNative { X = monitor.Work.Left, Y = monitor.Work.Top };
                    info.MinTrackSize = new PointNative { X = minTrackX, Y = minTrackY };
                    Marshal.StructureToPtr(info, lParam, true);
                }
            }

            return IntPtr.Zero;
        }
    }
}
