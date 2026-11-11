using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MintWall.Native;

namespace MintWall.Services;

/// <summary>
/// 视频壁纸宿主进程：每块屏一个播放窗口，挂到桌面图标层的最底部循环播放。
/// 独立进程的好处是界面点 × 交接后台后视频照常播放，停止由 VideoWallpaperService 控制。
/// </summary>
public static class VideoWallpaperHost
{
    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private static EventWaitHandle? _stopEvent;
    private static readonly List<HwndSource> Sources = new();

    public static int Run(string? videoPath)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
        {
            WorkerLog.Write("视频壁纸：找不到视频文件 " + videoPath);
            return 2;
        }

        using Mutex mutex = new(true, VideoWallpaperService.HostMutexName, out bool first);
        if (!first)
        {
            WorkerLog.Write("视频壁纸：已有宿主在运行，本次启动忽略。");
            return 0;
        }

        try
        {
            WritePid();
            RunCore(videoPath);
            return 0;
        }
        catch (Exception ex)
        {
            WorkerLog.Write("视频壁纸宿主异常退出：" + ex);
            return 1;
        }
        finally
        {
            DeletePid();
        }
    }

    private static void RunCore(string videoPath)
    {
        Application app = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += (_, _) => StartWindows(app, videoPath);
        app.Exit += (_, _) =>
        {
            foreach (HwndSource source in Sources)
            {
                source.Dispose();
            }
        };
        app.Run();
    }

    private static void StartWindows(Application app, string videoPath)
    {
        IntPtr layer = DesktopLayer.FindWallpaperLayer();
        if (layer == IntPtr.Zero)
        {
            WorkerLog.Write("视频壁纸：没找到桌面图标层，宿主退出。");
            app.Shutdown();
            return;
        }

        IReadOnlyList<DisplayMonitor> monitors = MonitorEnumeration.Enumerate();
        foreach (DisplayMonitor monitor in monitors)
        {
            CreateMonitorWindow(layer, monitor, videoPath);
        }

        KeepOnWallpaperLayer();
        DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => KeepOnWallpaperLayer();
        timer.Start();

        WorkerLog.Write($"视频壁纸已启动：{Path.GetFileName(videoPath)}，{monitors.Count} 块屏。");

        _stopEvent = new EventWaitHandle(false, EventResetMode.AutoReset, VideoWallpaperService.StopEventName);
        Thread listener = new(() =>
        {
            _stopEvent.WaitOne();
            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                WorkerLog.Write("视频壁纸：收到停止信号。");
                app.Shutdown();
            }));
        })
        {
            IsBackground = true,
            Name = "MintWall.VideoStop",
        };
        listener.Start();
    }

    /// <summary>Explorer 重建桌面层后句柄会变：定时把视频窗口挪到当前壁纸层，避免停止后残留或跑错层。</summary>
    private static void KeepOnWallpaperLayer()
    {
        IntPtr layer = DesktopLayer.FindCurrentWallpaperLayer();
        if (layer == IntPtr.Zero)
        {
            return;
        }

        foreach (HwndSource source in Sources)
        {
            if (DesktopLayer.ParentOf(source.Handle) != layer)
            {
                DesktopLayer.Reparent(source.Handle, layer);
            }
        }
    }

    private static void CreateMonitorWindow(IntPtr layer, DisplayMonitor monitor, string videoPath)
    {
        RECT bounds = monitor.Bounds;
        int width = Math.Max(1, bounds.Right - bounds.Left);
        int height = Math.Max(1, bounds.Bottom - bounds.Top);

        HwndSourceParameters parameters = new("MintWallVideoLayer")
        {
            ParentWindow = layer,
            WindowStyle = WS_CHILD | WS_VISIBLE,
            ExtendedWindowStyle = WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            PositionX = bounds.Left,
            PositionY = bounds.Top,
            Width = width,
            Height = height,
        };

        HwndSource source = new(parameters);
        MediaElement media = new()
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Manual,
            Stretch = Stretch.UniformToFill,
            Volume = 0,
            IsMuted = true,
            Source = new Uri(videoPath),
        };
        media.MediaFailed += (_, e) => WorkerLog.Write("视频壁纸播放失败：" + e.ErrorException?.Message);
        media.MediaEnded += (_, _) =>
        {
            media.Position = TimeSpan.Zero;
            media.Play();
        };

        source.RootVisual = media;
        media.Play();
        Sources.Add(source);
    }

    private static void WritePid()
    {
        try
        {
            File.WriteAllText(Path.Combine(DataPaths.Root, "video-host.pid"), Environment.ProcessId.ToString());
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void DeletePid()
    {
        try
        {
            File.Delete(Path.Combine(DataPaths.Root, "video-host.pid"));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
