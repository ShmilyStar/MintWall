using System.Runtime.InteropServices;
using System.Text;

namespace MintWall.Native;

[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

public static class NativeMethods
{
    public const int SPI_GETDESKWALLPAPER = 0x0073;
    public const int SPI_SETDESKWALLPAPER = 0x0014;
    public const int SPIF_UPDATEINIFILE = 0x01;
    public const int SPIF_SENDWININICHANGE = 0x02;

    public const uint SM_CXVIRTUALSCREEN = 79;
    public const uint SM_CYVIRTUALSCREEN = 80;
    public const uint SM_XVIRTUALSCREEN = 76;
    public const uint SM_YVIRTUALSCREEN = 77;

    public const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SystemParametersInfo(int action, int param, string? pvParam, int flags);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SystemParametersInfo(int action, int param, StringBuilder pvParam, int flags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetSystemMetrics(uint nIndex);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AllocConsole();

    public static void BroadcastEnvironmentChange()
    {
        SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, (string?)null, SPIF_UPDATEINIFILE | SPIF_SENDWININICHANGE);
    }
}
