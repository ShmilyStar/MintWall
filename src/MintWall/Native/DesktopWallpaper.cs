using System.Runtime.InteropServices;

namespace MintWall.Native;

// 值来自 shobjidl_core.h 的 DESKTOP_WALLPAPER_POSITION，不是注册表 WallpaperStyle 的 6/10/22。
public enum DesktopWallpaperPosition
{
    Center = 0,
    Tile = 1,
    Stretch = 2,
    Fit = 3,
    Fill = 4,
    Span = 5,
}

/// <summary>
/// Vtable order must match shobjidl_core.h exactly; unused tail slots are declared as
/// placeholders so the earlier indices stay aligned.
/// </summary>
[ComImport]
[Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDesktopWallpaper
{
    void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);

    [return: MarshalAs(UnmanagedType.LPWStr)]
    string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId);

    [return: MarshalAs(UnmanagedType.LPWStr)]
    string GetMonitorDevicePathAt(uint monitorIndex);

    void GetMonitorDevicePathCount(out uint count);

    void GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId, out RECT rect);

    void SetBackgroundColor(uint color);

    void GetBackgroundColor(out uint color);

    void SetPosition(DesktopWallpaperPosition position);

    void GetPosition(out DesktopWallpaperPosition position);

    void SetSlideshow([MarshalAs(UnmanagedType.Interface)] object? items);

    void GetSlideshow(out IntPtr items);

    void SetSlideshowOptions(uint options, uint tick);

    void GetSlideshowOptions(out uint options, out uint tick);

    void AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, uint direction);

    void GetStatus(out uint status);

    void Enable([MarshalAs(UnmanagedType.Bool)] bool enable);
}

public static class DesktopWallpaperFactory
{
    public static readonly Guid ClassId = new("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD");

    private static IDesktopWallpaper? _cached;

    public static IDesktopWallpaper? Create()
    {
        if (_cached is not null)
        {
            return _cached;
        }

        try
        {
            Type? type = Type.GetTypeFromCLSID(ClassId, throwOnError: false);
            if (type is null)
            {
                return null;
            }

            _cached = (IDesktopWallpaper?)Activator.CreateInstance(type);
            return _cached;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>explorer 重启后旧对象会失效，调用方报错时丢掉缓存；退出时主动释放 RCW。</summary>
    public static void Release()
    {
        IDesktopWallpaper? cached = _cached;
        _cached = null;
        if (cached is not null)
        {
            try
            {
                Marshal.ReleaseComObject(cached);
            }
            catch (ArgumentException)
            {
            }
        }
    }
}