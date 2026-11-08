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

    public const int SM_CXVIRTUALSCREEN = 79;
    public const int SM_CYVIRTUALSCREEN = 80;
    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;

    public const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SystemParametersInfo(int action, int param, string? pvParam, int flags);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SystemParametersInfo(int action, int param, StringBuilder pvParam, int flags);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AllocConsole();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQuerySessionInformation(IntPtr hServer, int sessionId, int infoClass, out IntPtr buffer, out int bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pointer);

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsSessionInfo
    {
        public uint SessionId;
        public long LogonTime;
    }

    /// <summary>当前会话的登录时间（UTC）；用来认出开机自启的那次无参数启动。</summary>
    public static DateTime? GetSessionLogonTime()
    {
        const int wtsCurrentSession = -1;
        const int wtsSessionInfo = 24;
        if (!WTSQuerySessionInformation(IntPtr.Zero, wtsCurrentSession, wtsSessionInfo, out IntPtr buffer, out _))
        {
            return null;
        }

        try
        {
            WtsSessionInfo info = Marshal.PtrToStructure<WtsSessionInfo>(buffer);
            return info.LogonTime > 0 ? DateTime.FromFileTimeUtc(info.LogonTime) : null;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    public static void BroadcastEnvironmentChange()
    {
        SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, (string?)null, SPIF_UPDATEINIFILE | SPIF_SENDWININICHANGE);
    }
}
