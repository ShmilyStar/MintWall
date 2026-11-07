using System.Runtime.InteropServices;

namespace WallpaperStudio.Native;

public enum DesktopWallpaperPosition
{
    Center = 0,
    Tile = 5,
    Fit = 6,
    Fill = 10,
    Span = 14,
}

/// <summary>
/// Vtable order must match desktopwallpaper.h exactly; extra slots are declared as
/// placeholders so the earlier indices stay aligned.
/// </summary>
[ComImport]
[Guid("B92B56A9-8B55-4E14-9A89-864BBB22F64B")]
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

    void GetSlideshowOptions(out uint options, out uint direction);

    void GetSlideshowCurrentItem([MarshalAs(UnmanagedType.LPWStr)] out string item);

    void AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, uint direction);

    void GetStatus(out uint status);

    [return: MarshalAs(UnmanagedType.LPWStr)]
    string GetMonitorIDAt(uint monitorIndex);
}

public static class DesktopWallpaperFactory
{
    public static readonly Guid ClassId = new("44AAE637-3983-4031-9F66-D4C6358D8EE1");

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
}
