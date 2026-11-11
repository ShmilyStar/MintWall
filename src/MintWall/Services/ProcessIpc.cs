using System.Diagnostics;
using System.Threading;

namespace MintWall.Services;

/// <summary>
/// 界面进程与轻量后台进程之间的交接：互斥量标明谁在跑，事件负责「激活窗口 / 停后台 / 后台已就绪」。
/// </summary>
public static class ProcessIpc
{
    public const string SingleInstanceMutexName = @"Local\MintWall.Desktop.SingleInstance";
    public const string ShowEventName = @"Local\MintWall.Desktop.ShowMainWindow";
    public const string WorkerMutexName = @"Local\MintWall.Desktop.Worker";
    public const string StopWorkerEventName = @"Local\MintWall.Desktop.StopWorker";

    public static bool WorkerIsRunning
    {
        get
        {
            try
            {
                using Mutex mutex = Mutex.OpenExisting(WorkerMutexName);
                return true;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return false;
            }
        }
    }

    /// <summary>界面进程是否已经在跑（只探测，不点亮窗口）。</summary>
    public static bool UiIsRunning
    {
        get
        {
            try
            {
                using Mutex mutex = Mutex.OpenExisting(SingleInstanceMutexName);
                return true;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return false;
            }
        }
    }

    /// <summary>界面要接管（或卸载清理）时让后台退出，并等它真的释放互斥量。</summary>
    public static void StopWorkerAndWait()
    {
        try
        {
            using EventWaitHandle stop = EventWaitHandle.OpenExisting(StopWorkerEventName);
            stop.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return;
        }

        Mutex? mutex = null;
        try
        {
            mutex = Mutex.OpenExisting(WorkerMutexName);
            if (mutex.WaitOne(8000))
            {
                mutex.ReleaseMutex();
            }
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
        catch (AbandonedMutexException)
        {
            // 后台被强杀：WaitOne 抛异常的同时已拿到所有权，释放后互斥量即消失。
            mutex?.ReleaseMutex();
        }
        finally
        {
            mutex?.Dispose();
        }
    }

    /// <summary>界面退出前拉起后台进程；等它就绪才算交接成功，失败时调用方退回隐藏窗口。</summary>
    public static bool TryStartWorker()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            return false;
        }

        // 每次交接独立事件名：共享名字时并发决策的 Reset 会抹掉别人已等到的就绪信号。
        string readyName = $@"Local\MintWall.Desktop.WorkerReady.{Guid.NewGuid():N}";
        using EventWaitHandle ready = new(false, EventResetMode.ManualReset, readyName);
        int pid = -1;
        try
        {
            Process? child = Process.Start(new ProcessStartInfo(exe, $"--worker --ready={readyName}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            pid = child?.Id ?? -1;
        }
        catch (Exception ex)
        {
            WorkerLog.Write("交接：后台进程启动失败：" + ex.Message);
            return false;
        }

        bool readyOk = ready.WaitOne(30000);
        if (!readyOk && pid > 0)
        {
            // 信号没等到但子进程还活着：多半仍在冷启动，交接照样成立。
            try
            {
                using Process child = Process.GetProcessById(pid);
                readyOk = !child.HasExited;
            }
            catch (ArgumentException)
            {
                readyOk = false;
            }
        }

        WorkerLog.Write($"交接：后台 pid={pid} 就绪={readyOk}");
        return readyOk;
    }
}