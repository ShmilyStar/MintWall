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
        using ScheduleWake wake = new();

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
            loops.Add(DesktopLoop(engine, gate, wake, profile, cts.Token));
        }

        foreach (MonitorSettings profile in settings.Lock.Monitors.Where(p => p.Schedule.Enabled))
        {
            loops.Add(LockLoop(engine, gate, wake, sessions, profile, cts.Token));
        }

        WorkerLog.Write($"后台进程已启动：桌面定时 {settings.Desktop.Monitors.Count(p => p.Schedule.Enabled)} 个，锁屏定时 {settings.Lock.Monitors.Count(p => p.Schedule.Enabled)} 个。");
        foreach (MonitorSettings profile in settings.Desktop.Monitors.Where(p => p.Schedule.Enabled))
        {
            WorkerLog.Write($"桌面·{profile.Label} 每 {Math.Clamp(profile.Schedule.IntervalMinutes, 1, 43200)} 分钟换一次。");
        }

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

    private static async Task DesktopLoop(AutoChangeEngine engine, SemaphoreSlim gate, ScheduleWake wake, MonitorSettings profile, CancellationToken ct)
    {
        TimeSpan interval = TimeSpan.FromMinutes(Math.Clamp(profile.Schedule.IntervalMinutes, 1, 43200));
        DateTime next = DateTime.UtcNow + interval;
        while (!ct.IsCancellationRequested)
        {
            if (!await WaitUntilAsync(next, wake, ct))
            {
                return;
            }

            next = DateTime.UtcNow + interval;
            bool changed = await TryChangeAsync(engine, gate, isLock: false, profile, ct);
            if (!changed)
            {
                // 屏幕未就绪或上一轮还没结束：不要白白等满一个间隔，20 秒后再试。
                next = DateTime.UtcNow.AddSeconds(20);
            }
        }
    }

    /// <summary>锁屏定时只在会话锁定后计时；解锁即停表，下次锁定重新计满一个间隔。</summary>
    private static async Task LockLoop(AutoChangeEngine engine, SemaphoreSlim gate, ScheduleWake wake, SessionMonitor sessions, MonitorSettings profile, CancellationToken ct)
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

            // 锁定期间重新起算；唤醒/时间跳变会打断等待，重新按墙上时间判断。
            DateTime next = DateTime.UtcNow + interval;
            while (!ct.IsCancellationRequested)
            {
                (bool stillLocked, Task stateChanged) = sessions.Snapshot();
                if (!stillLocked)
                {
                    break;
                }

                if (wake.TryArm(out Task signal))
                {
                    continue;
                }

                TimeSpan wait = next - DateTime.UtcNow;
                if (wait <= TimeSpan.Zero)
                {
                    next = DateTime.UtcNow + interval;
                    bool changedNow = await TryChangeAsync(engine, gate, isLock: true, profile, ct);
                    if (!changedNow)
                    {
                        next = DateTime.UtcNow.AddSeconds(20);
                    }

                    continue;
                }

                Task delay = Task.Delay(wait, ct);
                Task winner = await Task.WhenAny(delay, stateChanged, signal);
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                if (winner == stateChanged)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// 按墙上时间等一个长定时；只有系统唤醒/时间被调整时事件会提前打断，
    /// 平时不轮询，也不额外唤醒。
    /// </summary>
    private static Task<bool> WaitUntilAsync(DateTime deadline, ScheduleWake wake, CancellationToken ct)
        => ScheduleWait.UntilAsync(deadline, wake, () => DateTime.UtcNow, ct);

    private static async Task<bool> TryChangeAsync(AutoChangeEngine engine, SemaphoreSlim gate, bool isLock, MonitorSettings profile, CancellationToken ct)
    {
        bool acquired;
        try
        {
            acquired = await gate.WaitAsync(0, ct);
        }
        catch (OperationCanceledException)
        {
            return true;
        }

        if (!acquired)
        {
            // 另一次换图还在进行：跳过本轮，20 秒后重试。
            WorkerLog.Write($"{(isLock ? "锁屏" : "桌面")}·{profile.Label} 上一轮换图未结束，稍后重试。");
            return false;
        }

        try
        {
            if (!isLock && !engine.IsMonitorOnline(profile))
            {
                // 屏拔掉了：这块屏暂停，与界面收起 tab 停表一致。
                WorkerLog.Write($"桌面·{profile.Label} 暂时找不到对应显示器，20 秒后重试。");
                return false;
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

        return true;
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
/// <summary>
/// 定时循环的「提前醒来」信号：系统从休眠恢复或时间被调整时置位，
/// 让循环重新按墙上时间判断是否该补触发；平时不产生任何周期唤醒。
/// </summary>
internal sealed class ScheduleWake : IWakeSignal, IDisposable
{
    private readonly object _gate = new();
    private TaskCompletionSource _next = NewSignal();
    private bool _pending;

    public ScheduleWake()
    {
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.TimeChanged += OnTimeChanged;
    }

    /// <summary>取一个「下一次事件」的等待句柄；若事件已经发生过则返回 true，让调用方先重算。</summary>
    public bool TryArm(out Task signal)
    {
        lock (_gate)
        {
            signal = _next.Task;
            if (!_pending)
            {
                return false;
            }

            _pending = false;
            return true;
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            Raise();
        }
    }

    private void OnTimeChanged(object? sender, EventArgs e) => Raise();

    private void Raise()
    {
        TaskCompletionSource old;
        lock (_gate)
        {
            _pending = true;
            old = _next;
            _next = NewSignal();
        }

        old.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.TimeChanged -= OnTimeChanged;
    }
}
