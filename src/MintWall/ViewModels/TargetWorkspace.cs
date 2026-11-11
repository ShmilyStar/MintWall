using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MintWall.Models;
using MintWall.Services;

namespace MintWall.ViewModels;

/// <summary>
/// 一个壁纸目标（桌面或锁屏）的容器：每块显示器一个 MonitorWorkspace，顶栏 tab 切换就是切 Selected。
/// </summary>
public partial class TargetWorkspace : ObservableObject
{
    private readonly MainViewModel _host;
    private readonly Dictionary<string, MonitorWorkspace> _byMonitorId = new(StringComparer.Ordinal);
    private bool _sessionLocked;

    public TargetWorkspace(MainViewModel host, bool isLock)
    {
        _host = host;
        IsLock = isLock;
        Data = host.Settings.For(isLock);

        // 构造期直接写后备字段，避免 OnSyncLockScreenChanged 在界面绑定前就落盘。
        _syncLockScreen = Data.SyncLockScreen;
    }

    public bool IsLock { get; }

    public string Title => IsLock ? "锁屏" : "桌面";

    public TargetSettings Data { get; }

    public ObservableCollection<MonitorWorkspace> Monitors { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnyMonitor))]
    private MonitorWorkspace? _selected;

    /// <summary>仅桌面用：换这块屏的桌面壁纸时是否顺手写一次锁屏。</summary>
    [ObservableProperty]
    private bool _syncLockScreen;

    public bool HasAnyMonitor => Monitors.Count > 0;

    public int MonitorCount => Monitors.Count;

    public int TotalSourceCount => Monitors.Sum(m => m.SourceCount);

    public int TotalLibraryCount => Monitors.Sum(m => m.LibraryCount);

    public int TotalVideoCount => Monitors.Sum(m => m.Video.LocalCount);

    public int TotalVideoSourceCount => Monitors.Sum(m => m.Video.EnabledSourceCount);

    public string RootDir => DataPaths.WallpaperDirFor(IsLock);

    /// <summary>顶栏 tab 下方那句“能不能真的逐屏下发”的说明。</summary>
    public string PerMonitorNotice => _host.PerMonitorNotice;

    /// <summary>会话锁定时，锁屏目标的定时器才开始计时；桌面目标不受影响。</summary>
    public void NotifySessionLocked(bool locked)
    {
        _sessionLocked = locked;
        foreach (MonitorWorkspace workspace in Monitors)
        {
            workspace.NotifySessionLocked(locked);
        }
    }

    [RelayCommand]
    public void Refresh()
    {
        foreach (MonitorWorkspace workspace in Monitors)
        {
            workspace.Refresh();
        }
    }

    /// <summary>页面顶部的“换一张”：对当前选中的那块屏执行。</summary>
    [RelayCommand]
    private async Task FetchSelectedAsync()
    {
        if (Selected is { } workspace)
        {
            await workspace.FetchAsync(auto: false);
        }
    }

    [RelayCommand]
    private async Task ImportSelectedAsync()
    {
        if (Selected is { } workspace)
        {
            await workspace.ImportLocalAsync();
        }
    }

    [RelayCommand]
    private void OpenLibraryFolder() => Selected?.OpenLibraryFolder();

    public Task ImportPathAsync(string path) => Selected?.ImportPathAsync(path) ?? Task.CompletedTask;

    public MonitorWorkspace? ForMonitor(string monitorId) =>
        Monitors.FirstOrDefault(m => string.Equals(m.MonitorId, monitorId, StringComparison.Ordinal));

    /// <summary>桌面换图后顺带写锁屏，落到同一块屏的锁屏配置上。</summary>
    public async Task<string> SyncFromDesktopAsync(string imagePath, string monitorId, CancellationToken token)
    {
        MonitorWorkspace? workspace = ForMonitor(monitorId) ?? Selected;
        return workspace is null
            ? " · 锁屏未同步（没有可用屏幕）"
            : await workspace.SyncLockNoteAsync(imagePath, token);
    }

    public async Task RefreshPreviewsAsync(CancellationToken token)
    {
        foreach (MonitorWorkspace workspace in Monitors)
        {
            await workspace.RefreshPreviewAsync(token);
        }
    }

    public void Stop()
    {
        foreach (MonitorWorkspace workspace in Monitors)
        {
            workspace.Stop();
        }
    }

    public void SaveData()
    {
        foreach (MonitorWorkspace workspace in Monitors)
        {
            workspace.SaveData();
        }
    }

    public void RaiseCounts()
    {
        OnPropertyChanged(nameof(MonitorCount));
        OnPropertyChanged(nameof(TotalSourceCount));
        OnPropertyChanged(nameof(TotalLibraryCount));
        OnPropertyChanged(nameof(TotalVideoCount));
        OnPropertyChanged(nameof(TotalVideoSourceCount));
    }

    /// <summary>
    /// 按当前在线的屏幕补齐配置并重建 tab：老配置整体落到第一块屏，拔掉的屏只收起 tab、不删配置。
    /// </summary>
    public void Reconcile(IReadOnlyList<MonitorInfo> screens)
    {
        if (screens.Count == 0)
        {
            return;
        }

        MonitorSettings? legacy = TakeLegacy();
        AdoptByPosition(screens);

        foreach (MonitorInfo screen in screens)
        {
            MonitorSettings? data = Data.Monitors.FirstOrDefault(
                m => string.Equals(m.MonitorId, screen.Id, StringComparison.Ordinal));

            if (data is null)
            {
                data = legacy is null ? new MonitorSettings() : legacy;
                data.MonitorId = screen.Id;
                Data.Monitors.Add(data);
                legacy = null;
            }
            else if (legacy is not null)
            {
                MergeInto(data, legacy);
                legacy = null;
            }

            data.Label = screen.Label;
        }

        foreach (MonitorInfo screen in screens)
        {
            MonitorSettings data = Data.Monitors.First(m => string.Equals(m.MonitorId, screen.Id, StringComparison.Ordinal));
            if (!_byMonitorId.TryGetValue(screen.Id, out MonitorWorkspace? workspace))
            {
                workspace = new MonitorWorkspace(_host, IsLock, data);
                workspace.NotifySessionLocked(_sessionLocked);
                _byMonitorId[screen.Id] = workspace;
            }

            workspace.AttachScreen(screen);
        }

        // tab 顺序跟着屏幕枚举走；不在位的屏幕只收起 tab，配置继续留在 settings.json 里。
        List<MonitorWorkspace> ordered = new(screens.Count);
        foreach (MonitorInfo screen in screens)
        {
            if (_byMonitorId.TryGetValue(screen.Id, out MonitorWorkspace? workspace))
            {
                ordered.Add(workspace);
            }
        }

        bool sameOrder = ordered.Count == Monitors.Count
            && Enumerable.Range(0, ordered.Count).All(i => ReferenceEquals(ordered[i], Monitors[i]));
        if (!sameOrder)
        {
            // 屏幕没变时不动集合，避免每次刷新都让 tab 闪一下、选中项被重置。
            Monitors.Clear();
            foreach (MonitorWorkspace workspace in ordered)
            {
                Monitors.Add(workspace);
            }
        }

        foreach (KeyValuePair<string, MonitorWorkspace> entry in _byMonitorId)
        {
            if (!ordered.Contains(entry.Value))
            {
                entry.Value.Stop();
            }
        }

        MonitorWorkspace? current = Selected;
        Selected = current is not null && Monitors.Contains(current) ? current : Monitors.FirstOrDefault();
        OnPropertyChanged(nameof(HasAnyMonitor));
    }

    /// <summary>
    /// 屏幕的持久 key 全对不上了（换驱动、枚举顺序变化都会让设备名重新编号）：按位置认领老配置，
    /// 并把库目录搬到新 key，免得每次重启都把 tab 里的源、定时和样式清空。
    /// </summary>
    private void AdoptByPosition(IReadOnlyList<MonitorInfo> screens)
    {
        if (Data.Monitors.Count == 0)
        {
            return;
        }

        bool anyMatch = screens.Any(screen => Data.Monitors.Any(
            m => string.Equals(m.MonitorId, screen.Id, StringComparison.Ordinal)));

        if (anyMatch)
        {
            return;
        }

        int take = Math.Min(screens.Count, Data.Monitors.Count);
        bool changed = false;
        for (int i = 0; i < take; i++)
        {
            MonitorSettings data = Data.Monitors[i];
            string newId = screens[i].Id;

            if (string.Equals(data.MonitorId, newId, StringComparison.Ordinal))
            {
                continue;
            }

            string oldId = data.MonitorId;
            string from = DataPaths.LibraryDirFor(IsLock, oldId);
            string to = DataPaths.LibraryDirFor(IsLock, newId);

            // 用户自己选了库目录时，内置目录根本没在用，不用搬。
            if (string.IsNullOrEmpty(data.LibraryDirectory))
            {
                LibraryService.MoveLooseFiles(from, to);
                data.LastApplied = ReplaceDirectory(data.LastApplied, from, to);
                if (!IsLock)
                {
                    _host.RepointDesktopWallpaper(from, to);
                }
            }

            data.MonitorId = newId;
            changed = true;

            if (_byMonitorId.TryGetValue(oldId, out MonitorWorkspace? workspace))
            {
                _byMonitorId.Remove(oldId);
                _byMonitorId[newId] = workspace;
                workspace.RebuildLibrary();
            }
        }

        if (changed)
        {
            _host.Persist();
        }
    }

    private static string ReplaceDirectory(string path, string fromDir, string toDir)
    {
        string prefix = fromDir + Path.DirectorySeparatorChar;

        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(toDir, path[prefix.Length..])
            : path;
    }

    /// <summary>把旧版“一个目标一套配置”的字段读出来，读一次就清空。</summary>
    private MonitorSettings? TakeLegacy()
    {
        if (Data.IsSplit && Data.LegacySources is not { Count: > 0 } && Data.LegacySchedule is null)
        {
            return null;
        }

        MonitorSettings legacy = new()
        {
            Sources = Data.LegacySources ?? new List<SourceProfile>(),
            ActiveSourceId = Data.LegacyActiveSourceId,
            Schedule = Data.LegacySchedule ?? new ScheduleSettings(),
            Style = Data.LegacyStyle ?? WallpaperStyleMode.Fill,
            LastApplied = Data.LegacyLastApplied ?? string.Empty,
        };

        Data.LegacySources = null;
        Data.LegacyActiveSourceId = null;
        Data.LegacySchedule = null;
        Data.LegacyStyle = null;
        Data.LegacyLastApplied = null;

        return legacy.Sources.Count > 0 || !string.IsNullOrEmpty(legacy.LastApplied) || legacy.Schedule.Enabled
            ? legacy
            : null;
    }

    private static void MergeInto(MonitorSettings target, MonitorSettings legacy)
    {
        if (target.Sources.Count == 0 && legacy.Sources.Count > 0)
        {
            target.Sources = legacy.Sources;
            target.ActiveSourceId = legacy.ActiveSourceId;
        }

        if (string.IsNullOrEmpty(target.LastApplied))
        {
            target.LastApplied = legacy.LastApplied;
        }
    }

    partial void OnSyncLockScreenChanged(bool value)
    {
        Data.SyncLockScreen = value;
        _host.Persist();
        _host.StatusText = value
            ? "以后每次换桌面壁纸都会同步写锁屏。"
            : "锁屏不再跟随桌面，只在“锁屏”页按它自己的源和库换图。";
    }

    partial void OnSelectedChanged(MonitorWorkspace? value)
    {
        if (value is not null)
        {
            MirrorSelection(value.MonitorId);
            _host.StatusText = $"{Title}设置已切到“{value.Label}”，下面的源、库、定时和样式只作用于这块屏。";
        }
    }

    /// <summary>另一个目标跟着切到同一块屏：概览页一套 tab 同时看两块屏的桌面与锁屏。</summary>
    private void MirrorSelection(string monitorId)
    {
        TargetWorkspace other = IsLock ? _host.Desktop : _host.Lock;
        MonitorWorkspace? match = other.ForMonitor(monitorId);
        if (match is not null && !ReferenceEquals(other.Selected, match))
        {
            other.Selected = match;
        }
    }
}
