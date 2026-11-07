using Microsoft.Win32;
using Windows.ApplicationModel;

namespace MintWall.Services;

/// <summary>
/// 便携版写当前用户的 Run 项，不需要管理员权限；MSIX 版改用包里的 startupTask，
/// 这样系统右键卸载时自启项会跟着包一起被删掉。
/// </summary>
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MintWall";
    private const string LegacyValueName = "WallpaperStudio";
    private const string TaskId = "MintWallStartup";

    /// <summary>启动时清掉不再使用的 Run 项：改名前的旧值，以及包内已被 startupTask 接管的值。</summary>
    public static void RemoveLegacyValue()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        if (DataPaths.IsPackaged)
        {
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>卸载时清掉开机自启，不留指向已删除程序的启动项。</summary>
    public static void Remove()
    {
        if (DataPaths.IsPackaged)
        {
            FindTask()?.Disable();
        }

        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
        key?.DeleteValue(LegacyValueName, throwOnMissingValue: false);
    }

    public static bool IsEnabled()
    {
        if (DataPaths.IsPackaged)
        {
            StartupTaskState state = FindTask()?.State ?? StartupTaskState.Disabled;
            return state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        }

        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string value && value.Length > 0;
    }

    public static void SetEnabled(bool enabled)
    {
        if (DataPaths.IsPackaged)
        {
            SetEnabledAsync(enabled).GetAwaiter().GetResult();
            return;
        }

        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey);

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        string? exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            throw new InvalidOperationException("读不到程序自身路径。");
        }

        key.SetValue(ValueName, $"\"{exe}\"");
    }

    /// <summary>界面开关走这条：WinRT 请求期间不阻塞 UI 线程，系统弹确认时界面仍可响应。</summary>
    public static async Task SetEnabledAsync(bool enabled)
    {
        if (!DataPaths.IsPackaged)
        {
            SetEnabled(enabled);
            return;
        }

        StartupTask? task = await FindTaskAsync().ConfigureAwait(true)
            ?? throw new InvalidOperationException("包里没有声明启动任务，请重新安装 MSIX 版本。");

        if (!enabled)
        {
            task.Disable();
            return;
        }

        StartupTaskState state = await task.RequestEnableAsync().AsTask().ConfigureAwait(true);
        if (state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy)
        {
            return;
        }

        throw new InvalidOperationException(state switch
        {
            StartupTaskState.DisabledByUser => "系统设置里已关掉本程序的启动项，请到「设置 → 应用 → 启动」手动打开。",
            StartupTaskState.DisabledByPolicy => "这台电脑的策略禁止了本程序开机自启。",
            _ => "系统没有接受开机自启请求，请先手动打开一次程序再试。",
        });
    }

    private static StartupTask? FindTask() => FindTaskAsync().GetAwaiter().GetResult();

    private static async Task<StartupTask?> FindTaskAsync()
    {
        IReadOnlyList<StartupTask> tasks = await StartupTask.GetForCurrentPackageAsync().AsTask().ConfigureAwait(true);
        return tasks.FirstOrDefault(task => string.Equals(task.TaskId, TaskId, StringComparison.Ordinal));
    }
}