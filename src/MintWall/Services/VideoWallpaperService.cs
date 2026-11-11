using System.Diagnostics;
using System.IO;
using System.Threading;

namespace MintWall.Services;

/// <summary>
/// 视频壁纸的起停：宿主是独立进程，界面、后台、CLI 都通过命名对象控制它，
/// 界面退出后视频也能继续播。
/// </summary>
public static class VideoWallpaperService
{
    public const string HostMutexName = @"Local\MintWall.Desktop.VideoHost";
    public const string StopEventName = @"Local\MintWall.Desktop.StopVideo";
    private const string PidFileName = "video-host.pid";

    public static bool IsRunning
    {
        get
        {
            try
            {
                using Mutex mutex = Mutex.OpenExisting(HostMutexName);
                return true;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return false;
            }
        }
    }

    public static void Start(string videoPath)
    {
        if (!File.Exists(videoPath))
        {
            throw new FileNotFoundException("视频文件不存在。", videoPath);
        }

        Stop();

        string? exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            throw new InvalidOperationException("读不到程序自身路径。");
        }

        Process.Start(new ProcessStartInfo(exe, $"--video-wallpaper \"{videoPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        for (int i = 0; i < 25 && !IsRunning; i++)
        {
            Thread.Sleep(200);
        }

        if (!IsRunning)
        {
            throw new InvalidOperationException("视频壁纸宿主没有启动成功，详见日志 worker.log。");
        }
    }

    /// <summary>界面线程用：只停后台，等退出/强杀丢到线程池，避免切壁纸时卡一下。</summary>
    public static void StopInBackground()
    {
        if (!IsRunning)
        {
            return;
        }

        _ = Task.Run(Stop);
    }

    /// <summary>优雅让宿主退出；3 秒没退就按 pid 强杀。没在运行时直接返回。</summary>
    public static void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        try
        {
            using EventWaitHandle stop = EventWaitHandle.OpenExisting(StopEventName);
            stop.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }

        for (int i = 0; i < 15 && IsRunning; i++)
        {
            Thread.Sleep(200);
        }

        if (!IsRunning)
        {
            return;
        }

        try
        {
            string pidFile = Path.Combine(DataPaths.Root, PidFileName);
            if (!File.Exists(pidFile) || !int.TryParse(File.ReadAllText(pidFile).Trim(), out int pid))
            {
                return;
            }

            using Process host = Process.GetProcessById(pid);
            host.Kill();
            host.WaitForExit(3000);
        }
        catch
        {
            // 强杀是兜底，进程已经退出或拿不到句柄都不影响后续操作。
        }
    }
}
