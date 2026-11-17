using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
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
    private static readonly List<MediaElement> Players = new();

    // 锁屏/全屏应用时视频壁纸看不见，继续解码纯属浪费 CPU（软件合成机器上尤其明显）。
    private static bool _sessionLocked;
    private static bool _fullScreenBusy;
    private static bool _paused;

    private const int QUNS_BUSY = 2;
    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    private const int QUNS_PRESENTATION_MODE = 4;

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

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
            SystemEvents.SessionSwitch -= OnSessionSwitch;
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

        // 先量一次内嵌黑边（子进程软件渲染抓帧），再建窗口，避免先出现黑条再跳一下。
        Bars bars = DetectBars(videoPath);
        IReadOnlyList<DisplayMonitor> monitors = MonitorEnumeration.Enumerate();
        foreach (DisplayMonitor monitor in monitors)
        {
            CreateMonitorWindow(layer, monitor, videoPath, bars);
        }

        KeepOnWallpaperLayer();
        SystemEvents.SessionSwitch += OnSessionSwitch;
        DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) =>
        {
            KeepOnWallpaperLayer();
            RefreshFullScreenState();
        };
        timer.Start();

        WorkerLog.Write($"视频壁纸已启动：{Path.GetFileName(videoPath)}，{monitors.Count} 块屏。");

        _stopEvent = new EventWaitHandle(false, EventResetMode.AutoReset, VideoWallpaperService.StopEventName);
        Thread listener = new(() =>
        {
            _stopEvent.WaitOne();
            _stopEvent.Dispose();
            _stopEvent = null;
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

    /// <summary>锁屏/全屏应用时暂停解码；只有状态变化才动播放器，避免频繁 Play/Pause。</summary>
    private static void RefreshPlaybackState()
    {
        bool paused = _sessionLocked || _fullScreenBusy;
        if (paused == _paused)
        {
            return;
        }

        _paused = paused;
        foreach (MediaElement media in Players)
        {
            if (paused)
            {
                media.Pause();
            }
            else
            {
                media.Play();
            }
        }

        WorkerLog.Write(paused
            ? $"视频壁纸：暂停播放（{(_sessionLocked ? "会话锁定" : "全屏应用/演示")}）。"
            : "视频壁纸：恢复播放。");
    }

    private static void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock)
        {
            _sessionLocked = true;
            RefreshPlaybackState();
        }
        else if (e.Reason == SessionSwitchReason.SessionUnlock)
        {
            _sessionLocked = false;
            RefreshPlaybackState();
        }
    }

    /// <summary>全屏游戏/演示时桌面被遮住：用 shell 的通知状态判断，5 秒一次，代价可忽略。</summary>
    private static void RefreshFullScreenState()
    {
        try
        {
            if (SHQueryUserNotificationState(out int state) != 0)
            {
                return;
            }

            bool busy = state is QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE;
            if (busy == _fullScreenBusy)
            {
                return;
            }

            _fullScreenBusy = busy;
            RefreshPlaybackState();
        }
        catch (Exception)
        {
        }
    }

    private static void CreateMonitorWindow(IntPtr layer, DisplayMonitor monitor, string videoPath, Bars bars)
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

        // 根视觉用一层可裁剪的容器包住 MediaElement：内嵌黑边的视频放大后要裁掉溢出部分。
        Grid root = new() { Background = Brushes.Black, ClipToBounds = true };

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
        root.Children.Add(media);

        ApplyBars(media, bars);

        source.RootVisual = root;
        media.Play();
        Players.Add(media);
        Sources.Add(source);
    }

    private readonly record struct Bars(double Top, double Bottom, double Left, double Right);

    /// <summary>
    /// 一些片源自带上下（或左右）黑边：直接铺屏会在桌面留黑条。
    /// 在主进程里 RenderTargetBitmap 抓不到视频帧（硬件合成），所以交给 --video-bars 子进程
    /// 用软件渲染量一次比例，再用放大+裁剪把黑边推出画面（等同 Wallpaper Engine 的“填充”）。
    /// </summary>
    private static Bars DetectBars(string videoPath)
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            return default;
        }

        try
        {
            using Process process = Process.Start(new ProcessStartInfo(exe, $"--video-bars \"{videoPath}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            })!;

            string output = process.StandardOutput.ReadToEnd().Trim();
            if (!process.WaitForExit(15000))
            {
                process.Kill();
                WorkerLog.Write("视频壁纸：黑边检测超时，按无黑边处理。");
                return default;
            }

            string[] parts = output.Split(';');
            if (parts.Length != 4
                || !double.TryParse(parts[0], out double top)
                || !double.TryParse(parts[1], out double bottom)
                || !double.TryParse(parts[2], out double left)
                || !double.TryParse(parts[3], out double right))
            {
                WorkerLog.Write("视频壁纸：黑边检测输出无法解析：" + output);
                return default;
            }

            WorkerLog.Write($"视频壁纸：黑边检测 上{top:P0} 下{bottom:P0} 左{left:P0} 右{right:P0}");
            return new Bars(top, bottom, left, right);
        }
        catch (Exception ex)
        {
            WorkerLog.Write("视频壁纸：黑边检测失败 " + ex.Message);
            return default;
        }
    }

    private static void ApplyBars(MediaElement media, Bars bars)
    {
        double vertical = 1 - bars.Top - bars.Bottom;
        double horizontal = 1 - bars.Left - bars.Right;
        if (vertical <= 0.1 || horizontal <= 0.1)
        {
            return;
        }

        if (bars.Top + bars.Bottom < 0.02 && bars.Left + bars.Right < 0.02)
        {
            return;
        }

        // 放大到黑边出画；另一轴必然溢出，由 root 的 ClipToBounds 裁掉。
        double scale = Math.Max(1 / vertical, 1 / horizontal);
        media.RenderTransformOrigin = new Point(0.5, 0.5);
        media.RenderTransform = new ScaleTransform(scale, scale);
        WorkerLog.Write($"视频壁纸：检测到内嵌黑边，已放大 {scale:F3} 倍铺满。");
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
