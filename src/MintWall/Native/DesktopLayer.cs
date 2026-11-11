using System.Runtime.InteropServices;

namespace MintWall.Native;

/// <summary>
/// 桌面图标层：向 shell 发 0x052C 让 Explorer 生成承载壁纸的 WorkerW，
/// 把窗口挂到它底部就能垫在图标后面（视频壁纸的落点）。
/// </summary>
public static class DesktopLayer
{
    private const uint WM_SPAWN_WORKERW = 0x052C;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? windowTitle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

    /// <summary>图标层后面的壁纸层（与图标层同级的顶层 WorkerW）——视频挂这里才能既可见、又垫在图标下面。</summary>
    public static IntPtr FindWallpaperLayer()
    {
        IntPtr shell = GetShellWindow();
        if (shell == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        _ = SendMessageTimeout(shell, WM_SPAWN_WORKERW, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 2000, out _);

        // Explorer 建层是异步的：轮询最多 2 秒。
        for (int attempt = 0; attempt < 10; attempt++)
        {
            (_, IntPtr wallpaper) = FindLayers();
            if (wallpaper != IntPtr.Zero)
            {
                return wallpaper;
            }

            Thread.Sleep(200);
            _ = SendMessageTimeout(shell, WM_SPAWN_WORKERW, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 2000, out _);
        }

        // 退化：图标层自己也带壁纸，能显示但会盖住图标。
        (IntPtr icons, _) = FindLayers();
        return icons != IntPtr.Zero ? icons : shell;
    }

    /// <summary>图标层（含 SHELLDLL_DefView）和它下方同级的壁纸层。</summary>
    private static (IntPtr Icons, IntPtr Wallpaper) FindLayers()
    {
        IntPtr icons = IntPtr.Zero;
        _ = EnumWindows((hWnd, _) =>
        {
            if (FindWindowEx(hWnd, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero)
            {
                return true;
            }

            icons = hWnd;
            return false;
        }, IntPtr.Zero);

        // 顶层窗口里排在图标层后面的 WorkerW 就是壁纸层。
        IntPtr wallpaper = icons != IntPtr.Zero
            ? FindWindowEx(IntPtr.Zero, icons, "WorkerW", null)
            : IntPtr.Zero;

        return (icons, wallpaper);
    }

    /// <summary>当前壁纸层（不主动触发建层），定时校正父级用。</summary>
    public static IntPtr FindCurrentWallpaperLayer()
    {
        (IntPtr icons, IntPtr wallpaper) = FindLayers();
        if (wallpaper != IntPtr.Zero)
        {
            return wallpaper;
        }

        return icons;
    }

    public static IntPtr ParentOf(IntPtr hWnd) => GetParent(hWnd);

    /// <summary>Explorer 换了一层窗口时把视频窗口挪到新父级。</summary>
    public static void Reparent(IntPtr hWnd, IntPtr newParent) => _ = SetParent(hWnd, newParent);
}
