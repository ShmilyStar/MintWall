using System.Threading;
using MintWall.Services;

namespace MintWall;

/// <summary>
/// 自定义入口：CLI 与后台进程在加载 WPF 之前就分流，后台换图不再背着整个界面框架。
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            if (string.Equals(args[0], "--background", StringComparison.OrdinalIgnoreCase))
            {
                // 开机自启壳显式传入：只进后台，不加载 WPF、不弹主界面。
                if (ProcessIpc.UiIsRunning)
                {
                    return 0;
                }

                return WorkerRunner.Run();
            }

            if (string.Equals(args[0], "--worker", StringComparison.OrdinalIgnoreCase))
            {
                string? ready = args.Skip(1).FirstOrDefault(a => a.StartsWith("--ready=", StringComparison.OrdinalIgnoreCase));
                return WorkerRunner.Run(ready?["--ready=".Length..]);
            }

            if (string.Equals(args[0], "--video-wallpaper", StringComparison.OrdinalIgnoreCase))
            {
                return VideoWallpaperHost.Run(args.Length > 1 ? args[1] : null);
            }

            if (string.Equals(args[0], "--video-thumb", StringComparison.OrdinalIgnoreCase))
            {
                return VideoThumbnailService.Capture(
                    args.Length > 1 ? args[1] : null,
                    args.Length > 2 ? args[2] : null);
            }

            // 放线程池跑：避免任何同步上下文捕获导致 HTTP/WinRT 续延死锁。
            return Task.Run(() => CliRunner.RunAsync(args)).GetAwaiter().GetResult();
        }

        if (TryActivateExistingUi())
        {
            return 0;
        }

        if (ProcessIpc.WorkerIsRunning)
        {
            // 后台在跑时点图标意味着要回界面：先让它让位，再由 App 接管单实例与窗口。
            ProcessIpc.StopWorkerAndWait();
        }

        App app = new();
        app.InitializeComponent();
        return app.Run();
    }

    /// <summary>已有界面在跑就点亮它的窗口。这里只做探测，最终仲裁仍在 App 启动时。</summary>
    private static bool TryActivateExistingUi()
    {
        using EventWaitHandle show = new(false, EventResetMode.AutoReset, ProcessIpc.ShowEventName);
        using Mutex mutex = new(false, ProcessIpc.SingleInstanceMutexName);
        bool owned;
        try
        {
            owned = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            owned = true;
        }

        if (owned)
        {
            mutex.ReleaseMutex();
            return false;
        }

        show.Set();
        return true;
    }
}