using System.Threading;
using Microsoft.Win32;
using MintWall.Models;

namespace MintWall.Services;

/// <summary>
/// 轻量后台进程：不加载 WPF，只负责定时换图。界面点 × 后退出、由它接管；
/// 再点程序图标时它收到事件让位，界面接回。登录自启也直接进这里，不弹窗口。
/// </summary>
public static class WorkerRunner
{
    public static int Run(string? readyName = null)
    {
        using Mutex mutex = new(true, ProcessIpc.WorkerMutexName, out bool first);
        if (!first)
        {
            WorkerLog.Write("已有后台进程在跑，本次 --worker 直接退出。");
            return 0;
        }

        try
        {
            RunCore(readyName);
            return 0;
        }
        catch (Exception ex)
        {
            WorkerLog.Write("后台进程异常退出：" + ex);
            return 1;
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private static void RunCore(string? readyName)
    {
        using EventWaitHandle stop = new(false, EventResetMode.AutoReset, ProcessIpc.StopWorkerEventName);
        using CancellationTokenSource cts = new();
        using SessionMonitor sessions = new();

        AppSettings settings = new ConfigStore().Load();
        RetargetContainerPaths(settings);
        // 就绪信号放在 COM 激活/补壁纸之前：那些冷启动可能十几秒，界面不该等它们才敢退出。
        SignalReady(readyName);
        using AutoChangeEngine engine = new(settings);

        try
        {
            engine.RestoreLastDesktop();
        }
        catch (Exception ex)
        {
            WorkerLog.Write("恢复桌面壁纸失败：" + ex.Message);
        }

        using SemaphoreSlim gate = new(1, 1);
        List<Task> loops = new();
        foreach (MonitorSettings profile in settings.Desktop.Monitors.Where(p => p.Schedule.Enabled))
        {
            loops.Add(DesktopLoop(engine, gate, profile, cts.Token));
        }

        foreach (MonitorSettings profile in settings.Lock.Monitors.Where(p => p.Schedule.Enabled))
        {
            loops.Add(LockLoop(engine, gate, sessions, profile, cts.Token));
        }

        WorkerLog.Write($"后台进程已启动：桌面定时 {settings.Desktop.Monitors.Count(p => p.Schedule.Enabled)} 个，锁屏定时 {settings.Lock.Monitors.Count(p => p.Schedule.Enabled)} 个。");

        stop.WaitOne();
        WorkerLog.Write("收到让位信号，后台进程退出。");
        cts.Cancel();
        try
        {
            Task.WaitAll(loops.ToArray(), 5000);
        }
        catch (AggregateException)
        {
        }
    }

    private static void SignalReady(string? readyName)
    {
        if (readyName is null)
        {
            return;
        }

        try
        {
            using EventWaitHandle ready = EventWaitHandle.OpenExisting(readyName);
            ready.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            WorkerLog.Write("交接：就绪事件已不存在（界面先超时退出了）。");
        }
    }

    private static async Task DesktopLoop(AutoChangeEngine engine, SemaphoreSlim gate, MonitorSettings profile, CancellationToken ct)
    {
        TimeSpan interval = TimeSpan.FromMinutes(Math.Clamp(profile.Schedule.IntervalMinutes, 1, 43200));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await TryChangeAsync(engine, gate, isLock: false, profile, ct);
        }
    }

    /// <summary>锁屏定时只在会话锁定后计时；解锁即停表，下次锁定重新计满一个间隔。</summary>
    private static async Task LockLoop(AutoChangeEngine engine, SemaphoreSlim gate, SessionMonitor sessions, MonitorSettings profile, CancellationToken ct)
    {
        TimeSpan interval = TimeSpan.FromMinutes(Math.Clamp(profile.Schedule.IntervalMinutes, 1, 43200));
        while (!ct.IsCancellationRequested)
        {
            (bool locked, Task changed) = sessions.Snapshot();
            if (!locked)
            {
                try
                {
                    await changed.WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            Task delay = Task.Delay(interval, ct);
            Task winner = await Task.WhenAny(delay, changed.WaitAsync(ct));
            if (ct.IsCancellationRequested)
            {
                return;
            }

            if (winner != delay)
            {
                continue;
            }

            await TryChangeAsync(engine, gate, isLock: true, profile, ct);
        }
    }

    private static async Task TryChangeAsync(AutoChangeEngine engine, SemaphoreSlim gate, bool isLock, MonitorSettings profile, CancellationToken ct)
    {
        bool acquired;
        try
        {
            acquired = await gate.WaitAsync(0, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!acquired)
        {
            // 另一次换图还在进行：跳过本轮，与界面忙碌时的行为一致。
            return;
        }

        try
        {
            if (!isLock && !engine.IsMonitorOnline(profile))
            {
                // 屏拔掉了：这块屏暂停，与界面收起 tab 停表一致。
                return;
            }

            await engine.ChangeAsync(isLock, profile, ct);
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            WorkerLog.Write($"{(isLock ? "锁屏" : "桌面")}·{profile.Label} 定时换图失败：{ex.Message}");
        }
        finally
        {
            gate.Release();
        }
    }

    private static void RetargetContainerPaths(AppSettings settings)
    {
        if (!DataPaths.IsPackaged)
        {
            return;
        }

        foreach (TargetSettings data in new[] { settings.Desktop, settings.Lock })
        {
            foreach (MonitorSettings profile in data.Monitors)
            {
                profile.LastApplied = DataPaths.Retarget(profile.LastApplied);
                profile.LibraryDirectory = DataPaths.Retarget(profile.LibraryDirectory);
                profile.SaveDirectory = DataPaths.Retarget(profile.SaveDirectory);
            }
        }
    }
}

/// <summary>跟踪会话锁定状态；SystemEvents 自带消息线程，无 WPF 也能收到锁定/解锁事件。</summary>
internal sealed class SessionMonitor : IDisposable
{
    private readonly object _gate = new();
    private bool _locked;
    private TaskCompletionSource _changed = NewSignal();

    public SessionMonitor()
    {
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    /// <summary>状态和「下次变更信号」原子地一起取，避免读到状态后、挂等待前刚好解锁的竞态。</summary>
    public (bool Locked, Task Changed) Snapshot()
    {
        lock (_gate)
        {
            return (_locked, _changed.Task);
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is not (SessionSwitchReason.SessionLock or SessionSwitchReason.SessionUnlock))
        {
            return;
        }

        bool locked = e.Reason == SessionSwitchReason.SessionLock;
        lock (_gate)
        {
            if (_locked == locked)
            {
                return;
            }

            _locked = locked;
            TaskCompletionSource old = _changed;
            _changed = NewSignal();
            old.TrySetResult();
        }

        WorkerLog.Write(locked ? "会话已锁定，锁屏定时开始计时。" : "会话已解锁，锁屏定时暂停。");
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Dispose() => SystemEvents.SessionSwitch -= OnSessionSwitch;
}