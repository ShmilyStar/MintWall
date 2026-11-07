using System.Runtime.InteropServices;
using System.Text;

namespace WallpaperStudio.Native;

public sealed record DisplayMonitor(RECT Bounds, bool IsPrimary);

public static class MonitorEnumeration
{
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprc, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    private const uint MONITORINFOF_PRIMARY = 0x1;

    public static IReadOnlyList<DisplayMonitor> Enumerate()
    {
        List<DisplayMonitor> monitors = new();

        bool Callback(IntPtr hMonitor, IntPtr hdc, ref RECT bounds, IntPtr data)
        {
            MONITORINFOEX info = new() { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMonitor, ref info))
            {
                monitors.Add(new DisplayMonitor(info.rcMonitor, (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
            }

            return true;
        }

        _ = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, new MonitorEnumProc(Callback), IntPtr.Zero);
        return monitors;
    }

    public static string Describe(RECT rect)
    {
        int width = Math.Max(0, rect.Right - rect.Left);
        int height = Math.Max(0, rect.Bottom - rect.Top);
        return width == 0 || height == 0 ? "未识别" : $"{width} × {height}";
    }

    public static int VirtualWidth()
    {
        RECT union = Union(Enumerate());
        return Math.Max(0, union.Right - union.Left);
    }

    public static RECT Union(IReadOnlyList<DisplayMonitor> monitors)
    {
        if (monitors.Count == 0)
        {
            return default;
        }

        RECT result = monitors[0].Bounds;
        foreach (DisplayMonitor monitor in monitors.Skip(1))
        {
            result.Left = Math.Min(result.Left, monitor.Bounds.Left);
            result.Top = Math.Min(result.Top, monitor.Bounds.Top);
            result.Right = Math.Max(result.Right, monitor.Bounds.Right);
            result.Bottom = Math.Max(result.Bottom, monitor.Bounds.Bottom);
        }

        return result;
    }
}
