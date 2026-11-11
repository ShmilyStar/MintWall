using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using MintWall.Models;
using MintWall.Native;

namespace MintWall.Services;

public sealed class WallpaperService
{
    private const uint WM_SETTINGCHANGE = 0x001A;
    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    private IDesktopWallpaper? _api;

    public WallpaperService()
    {
        _api = DesktopWallpaperFactory.Create();
    }

    public bool SupportsPerMonitor => _api is not null;

    public string EngineText => SupportsPerMonitor
        ? "IDesktopWallpaper（支持逐显示器）"
        : "SystemParametersInfo（仅整屏，未取到逐显示器接口）";

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        List<MonitorInfo> list = _api is not null ? MonitorsFromCom() : MonitorsFromWin32();

        if (list.Count == 0)
        {
            list.Add(new MonitorInfo
            {
                Id = Wallpapers.AllMonitors,
                Index = 0,
                Label = "全部显示器",
                Width = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN),
                Height = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN),
                CurrentWallpaper = ReadLegacyWallpaper(),
                PerMonitorSupported = false,
            });
        }

        return list;
    }

    private List<MonitorInfo> MonitorsFromCom()
    {
        List<MonitorInfo> list = new();
        try
        {
            _api!.GetMonitorDevicePathCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                string id = _api.GetMonitorDevicePathAt(i);

                // 用 SPI 设过壁纸后，接口会多列一个 legacy_monitor_001 伪屏，它没有 RECT 也设不了。
                if (id.StartsWith("legacy_monitor", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool primary = false;
                int width = 0;
                int height = 0;
                try
                {
                    _api.GetMonitorRECT(id, out RECT rect);
                    primary = rect.Left == 0 && rect.Top == 0;
                    width = Math.Max(0, rect.Right - rect.Left);
                    height = Math.Max(0, rect.Bottom - rect.Top);
                }
                catch (Exception ex)
                {
                    // 单块屏取不到尺寸不代表接口失效，继续枚举其余的屏。
                    LastError = ex.Message;
                }

                string current = string.Empty;
                try
                {
                    current = _api.GetWallpaper(id);
                }
                catch
                {
                    // Some drivers throw for a detached monitor; leave the field empty.
                }

                list.Add(new MonitorInfo
                {
                    Id = id,
                    Index = (int)i,
                    Label = $"显示器 {list.Count + 1}{(primary ? "（主）" : string.Empty)}",
                    Width = width,
                    Height = height,
                    CurrentWallpaper = current,
                    PerMonitorSupported = true,
                });
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            if (ex is InvalidComObjectException)
            {
                RefreshApi();
            }
        }

        return list;
    }

    private List<MonitorInfo> MonitorsFromWin32()
    {
        List<DisplayMonitor> raw = MonitorEnumeration.Enumerate().ToList();
        string legacy = ReadLegacyWallpaper();
        List<MonitorInfo> list = new();

        for (int index = 0; index < raw.Count; index++)
        {
            DisplayMonitor monitor = raw[index];
            int width = monitor.Bounds.Right - monitor.Bounds.Left;
            int height = monitor.Bounds.Bottom - monitor.Bounds.Top;

            list.Add(new MonitorInfo
            {
                // \\.\DISPLAYnn 每次会话都可能换号，拿它当持久 key 会让每块屏的配置和库目录跟着漂移。
                Id = raw.Count == 1 ? Wallpapers.Primary : $"win32:screen-{index + 1}-{width}x{height}",
                Index = index,
                Label = $"显示器 {index + 1}{(monitor.IsPrimary ? "（主）" : string.Empty)}",
                Width = width,
                Height = height,
                CurrentWallpaper = legacy,
                PerMonitorSupported = false,
            });
        }

        return list;
    }

    public void Apply(string filePath, string monitorId, WallpaperStyleMode style)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("壁纸文件不存在", filePath);
        }

        // 视频宿主盖在壁纸上方，静态换图前先收掉，否则看起来像"换了没反应"；
        // 停止要等退出/强杀，放到后台做，避免界面卡住。
        VideoWallpaperService.StopInBackground();

        WriteStyle(style);

        try
        {
            ApplyCore(filePath, monitorId, style);
        }
        catch (InvalidComObjectException)
        {
            // explorer 重启会让旧对象失效，重建后重试一次。
            if (RefreshApi() is null)
            {
                throw new NotSupportedException(
                    "IDesktopWallpaper 接口暂时不可用，无法设置壁纸，请稍后重试。");
            }

            ApplyCore(filePath, monitorId, style);
        }
    }

    private void ApplyCore(string filePath, string monitorId, WallpaperStyleMode style)
    {
        bool all = monitorId is Wallpapers.AllMonitors or Wallpapers.Primary || monitorId.StartsWith("win32:", StringComparison.Ordinal);

        if (all)
        {
            if (_api is not null)
            {
                // COM 一次下发即可；再走 SPI 会让桌面肉眼可见地刷两次。
                _api.SetPosition(ToPosition(style));
                _api.SetWallpaper(null, filePath);
            }
            else
            {
                NativeMethods.SystemParametersInfo(
                    NativeMethods.SPI_SETDESKWALLPAPER,
                    0,
                    filePath,
                    NativeMethods.SPIF_UPDATEINIFILE | NativeMethods.SPIF_SENDWININICHANGE);
            }

            return;
        }

        if (_api is null)
        {
            throw new NotSupportedException(
                "本系统缺少 IDesktopWallpaper 接口（常见于 LTSC / 精简版 Windows），无法逐显示器设置，只能整屏统一设置。");
        }

        _api.SetPosition(ToPosition(style));
        _api.SetWallpaper(monitorId, filePath);
        BroadcastDesktopChange();
    }

    private IDesktopWallpaper? RefreshApi()
    {
        DesktopWallpaperFactory.Release();
        _api = DesktopWallpaperFactory.Create();
        return _api;
    }

    public string CurrentWallpaperOf(string monitorId)
    {
        if (_api is null || monitorId == Wallpapers.AllMonitors)
        {
            return ReadLegacyWallpaper();
        }

        try
        {
            return _api.GetWallpaper(monitorId);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            if (ex is InvalidComObjectException)
            {
                RefreshApi();
            }

            return string.Empty;
        }
    }

    public string ReadLegacyWallpaper()
    {
        StringBuilder buffer = new(520);
        bool ok = NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETDESKWALLPAPER, buffer.Capacity, buffer, 0);
        string path = ok ? buffer.ToString() : string.Empty;

        if (string.IsNullOrEmpty(path))
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
                path = key?.GetValue("Wallpaper") as string ?? string.Empty;
            }
            catch
            {
                path = string.Empty;
            }
        }

        return path;
    }

    public WallpaperStyleMode ReadStyle()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            string value = key?.GetValue("WallpaperStyle") as string ?? "10";
            return value switch
            {
                "0" => WallpaperStyleMode.Center,
                "1" or "3" => WallpaperStyleMode.Tile,
                "2" => WallpaperStyleMode.Stretch,
                "6" => WallpaperStyleMode.Fit,
                "22" => WallpaperStyleMode.Span,
                _ => WallpaperStyleMode.Fill,
            };
        }
        catch
        {
            return WallpaperStyleMode.Fill;
        }
    }

    public string? LastError { get; private set; }

    private static void WriteStyle(WallpaperStyleMode style)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.CreateSubKey(@"Control Panel\Desktop");
            if (key is null)
            {
                return;
            }

            (string wallpaperStyle, string tile) = style switch
            {
                WallpaperStyleMode.Center => ("0", "0"),
                WallpaperStyleMode.Tile => ("1", "1"),
                WallpaperStyleMode.Stretch => ("2", "0"),
                WallpaperStyleMode.Fit => ("6", "0"),
                WallpaperStyleMode.Span => ("22", "0"),
                _ => ("10", "0"),
            };

            key.SetValue("WallpaperStyle", wallpaperStyle, RegistryValueKind.String);
            key.SetValue("TileWallpaper", tile, RegistryValueKind.String);
        }
        catch
        {
            // Style is a preference, not a prerequisite for setting the picture.
        }
    }

    private static DesktopWallpaperPosition ToPosition(WallpaperStyleMode style) => style switch
    {
        WallpaperStyleMode.Center => DesktopWallpaperPosition.Center,
        WallpaperStyleMode.Tile => DesktopWallpaperPosition.Tile,
        WallpaperStyleMode.Stretch => DesktopWallpaperPosition.Stretch,
        WallpaperStyleMode.Fit => DesktopWallpaperPosition.Fit,
        WallpaperStyleMode.Span => DesktopWallpaperPosition.Span,
        _ => DesktopWallpaperPosition.Fill,
    };

    private static void BroadcastDesktopChange()
    {
        // 广播最多等 1 秒，放后台发，避免点“换一张”后界面停顿。
        _ = Task.Run(() =>
        {
            try
            {
                _ = SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, "Desktop", SMTO_ABORTIFHUNG, 1000, out _);
            }
            catch
            {
                // Broadcast is best-effort.
            }
        });
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeoutMs, out IntPtr result);
}
