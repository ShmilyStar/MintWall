using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using MintWall.Models;
using MintWall.Services;

namespace MintWall.ViewModels;

/// <summary>
/// 应用外壳：导航、显示器枚举、共享设置、活动日志与忙碌状态。
/// 桌面与锁屏各是一个 TargetWorkspace，里面再按显示器分成多套源、库、定时与样式。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private const int ActivityLimit = 200;

    private readonly ConfigStore _config = new();
    private readonly WallpaperService _wallpaper = new();
    private readonly Dictionary<string, string> _pageAliases = new(StringComparer.Ordinal)
    {
        ["library"] = "desktoplib",
        ["sources"] = "desktopsrc",
        ["monitors"] = "home",
        ["schedule"] = "home",
        ["desktop"] = "home",
        ["lock"] = "home",
    };

    private CancellationTokenSource? _cts;
    private MonitorWorkspace? _editingWorkspace;
    private IReadOnlyList<MonitorInfo> _screens = Array.Empty<MonitorInfo>();

    public MainViewModel()
    {
        NavItems = BuildNavItems();
        Settings = _config.Load();
        DataPaths.MigrateLegacyAllFolders();

        EngineText = _wallpaper.EngineText;
        CurrentWallpaperText = _wallpaper.ReadLegacyWallpaper();
        _encode = Settings.Encode;
        _libraryLimit = Math.Clamp(Settings.LibraryLimit, 10, 100000);
        _desktopStyle = Settings.DesktopStyle;

        // 启动项以注册表为准：用户可能在别处删掉了自启，界面就跟着显示关闭。
        StartupService.RemoveLegacyValue();
        _startWithWindows = StartupService.IsEnabled();
        Settings.StartWithWindows = _startWithWindows;

        Desktop = new TargetWorkspace(this, false);
        Lock = new TargetWorkspace(this, true);

        RefreshMonitors();
        MigrateLooseLibraries();
        RepairMovedWallpaperPath();

        _currentPage = MigratePage(Settings.LastPage);
        _selectedNavItem = NavItems.FirstOrDefault(item => item is NavLink link && link.Key == CurrentPage);

        RaiseCounts();
        _ = RefreshAllPreviewsAsync(CancellationToken.None);
        Log("程序已启动。");
        StatusText = "就绪。壁纸库、壁纸源和概览都按显示器分 tab，每块屏都有自己的源、库、定时和样式。";
    }

    /// <summary>Raised when a workspace asks the UI to open the source editor dialog.</summary>
    public event Action<SourceProfile, bool>? EditSourceRequested;

    public AppSettings Settings { get; }

    public TargetWorkspace Desktop { get; }

    public TargetWorkspace Lock { get; }

    public WallpaperService Wallpaper => _wallpaper;

    /// <summary>当前在线的屏幕，顶栏 tab 与逐屏状态提示都来自这里。</summary>
    public ObservableCollection<MonitorInfo> Monitors { get; } = new();

    public ObservableCollection<string> Activity { get; } = new();

    public IReadOnlyList<NavItem> NavItems { get; }

    public IReadOnlyList<KeyValuePair<WallpaperStyleMode, string>> StyleOptions { get; } = new[]
    {
        new KeyValuePair<WallpaperStyleMode, string>(WallpaperStyleMode.Fill, "填充（裁掉多余部分，铺满屏幕）"),
        new KeyValuePair<WallpaperStyleMode, string>(WallpaperStyleMode.Fit, "适应（完整显示，可能留边）"),
        new KeyValuePair<WallpaperStyleMode, string>(WallpaperStyleMode.Stretch, "拉伸（变形填满）"),
        new KeyValuePair<WallpaperStyleMode, string>(WallpaperStyleMode.Center, "居中（不缩放）"),
        new KeyValuePair<WallpaperStyleMode, string>(WallpaperStyleMode.Tile, "平铺（重复贴图）"),
        new KeyValuePair<WallpaperStyleMode, string>(WallpaperStyleMode.Span, "跨越（多屏铺开）"),
    };

    public IReadOnlyList<KeyValuePair<EncodePreference, string>> EncodeOptions { get; } = new[]
    {
        new KeyValuePair<EncodePreference, string>(EncodePreference.Auto, "自动（原格式优先，webp 会转码）"),
        new KeyValuePair<EncodePreference, string>(EncodePreference.Jpeg, "强制 JPG（体积小，锁屏可用）"),
        new KeyValuePair<EncodePreference, string>(EncodePreference.Png, "强制 PNG（无损，体积大）"),
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDesktopPage))]
    [NotifyPropertyChangedFor(nameof(IsLockPage))]
    private string _currentPage = "home";

    [ObservableProperty]
    private NavItem? _selectedNavItem;

    [ObservableProperty]
    private bool _busy;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _probeReport = "还没有测试过接口。在“桌面”或“锁屏”页选中一个源后点“测试此源”，这里会显示跳转链路、Content-Type、尺寸和入库格式。";

    [ObservableProperty]
    private string _engineText = string.Empty;

    [ObservableProperty]
    private string _environmentReport = string.Empty;

    [ObservableProperty]
    private string _currentWallpaperText = string.Empty;

    [ObservableProperty]
    private EncodePreference _encode;

    [ObservableProperty]
    private int _libraryLimit = 300;

    [ObservableProperty]
    private WallpaperStyleMode _desktopStyle = WallpaperStyleMode.Fill;

    [ObservableProperty]
    private bool _startWithWindows;

    public string DataLocation => DataPaths.Root;

    public string AppVersion => "v" + (typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

    public bool IsPackaged => DataPaths.IsPackaged;

    public bool SupportsPerMonitor => _wallpaper.SupportsPerMonitor;

    public bool IsDesktopPage => CurrentPage == "desktop";

    public bool IsLockPage => CurrentPage == "lock";

    public int FavoriteCount => Settings.Favorites.Count;

    public bool CanOperate => !Busy;

    public string MonitorSummary => Monitors.Count switch
    {
        0 => "没有识别到显示器",
        1 => "1 块屏：" + Monitors[0].Label,
        _ => $"{Monitors.Count} 块屏：" + string.Join("、", Monitors.Select(m => m.Label)),
    };

    public string PerMonitorNotice => SupportsPerMonitor
        ? "系统支持逐显示器下发，每个 tab 的设置只影响那块屏。"
        : "这台系统没注册 IDesktopWallpaper，只能整屏下发：每块屏的配置会分别保存，但实际生效的是最后一次写入的那张。";

    public string LockApiText => LockScreenService.IsApiAvailable
        ? "可用（Windows.System.UserProfile.LockScreen）"
        : "异常：" + LockScreenService.ApiError;

    [RelayCommand]
    private void ActivatePage(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        CurrentPage = key;
        SelectedNavItem = NavItems.FirstOrDefault(item => item is NavLink link && link.Key == key);
    }

    [RelayCommand]
    private void RefreshAll()
    {
        RefreshMonitors();
        Desktop.Refresh();
        Lock.Refresh();
        StatusText = $"已重新识别 {_screens.Count} 块屏，并刷新两个目标的壁纸库与当前壁纸。";
    }

    /// <summary>快捷动作：把当前桌面壁纸写进锁屏，不往锁屏库加图片。</summary>
    [RelayCommand]
    private void PushDesktopToLock()
    {
        if (!File.Exists(CurrentWallpaperText))
        {
            StatusText = "读不到当前桌面壁纸文件，无法同步到锁屏。";
            return;
        }

        string monitorId = Desktop.Selected?.MonitorId ?? Wallpapers.AllMonitors;

        _ = RunAsync("正在把当前桌面壁纸写入锁屏…", async token =>
        {
            string note = await Lock.SyncFromDesktopAsync(CurrentWallpaperText, monitorId, token);
            string message = Lock.Selected?.Message ?? string.Empty;
            StatusText = string.IsNullOrEmpty(message) ? note.Trim(' ', '·') : message;
            Log("锁屏 ← 当前桌面壁纸：" + StatusText);
        });
    }

    /// <summary>快捷动作：把系统当前锁屏图直接设成这块屏的桌面壁纸，不往桌面库加图片。</summary>
    [RelayCommand]
    private void PushLockToDesktop()
    {
        string? path = LockScreenService.ReadCurrentPath();
        MonitorWorkspace? target = Desktop.Selected;
        if (target is null || path is null || !File.Exists(path))
        {
            StatusText = "读不到当前锁屏壁纸文件，无法用到桌面。";
            return;
        }

        _ = RunAsync("正在把当前锁屏壁纸设为桌面…", async token =>
        {
            // 先复制到桌面缓存：锁屏换图会清掉旧缓存，直接引用会让桌面壁纸文件消失。
            string copy = Path.Combine(DataPaths.CacheDirFor(false), Path.GetFileName(path));
            File.Copy(path, copy, overwrite: true);
            await target.ApplyPathAsync(copy, "已用锁屏壁纸", token);
        });
    }

    [RelayCommand]
    private void OpenDataFolder() => OpenInExplorer(DataPaths.Root);

    [RelayCommand]
    private void OpenSystemLockSettings()
    {
        OpenInExplorer("ms-settings:lockscreen");
        StatusText = "已打开系统“锁屏界面”设置。若那里选的是“Windows 聚焦”，程序写入会被系统忽略。";
    }

    [RelayCommand]
    private void OpenSystemBackgroundSettings()
    {
        // Win10 上 ms-settings:background 只会落到设置首页，带 personalization 前缀才进「背景」。
        OpenInExplorer("ms-settings:personalization-background");
        StatusText = "已打开系统“背景”设置，可以在那里看或改桌面壁纸与填充方式。";
    }

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private void CancelOperation()
    {
        _cts?.Cancel();
        StatusText = "已取消当前请求。";
    }

    /// <summary>由 MonitorWorkspace 调用：打开源编辑对话框并记住回写给哪块屏。</summary>
    public void RequestEditSource(SourceProfile draft, bool isNew, MonitorWorkspace owner)
    {
        _editingWorkspace = owner;
        EditSourceRequested?.Invoke(draft, isNew);
    }

    public void CommitSourceEdit(SourceProfile edited, bool isNew)
    {
        MonitorWorkspace? target = _editingWorkspace ?? Desktop.Selected;
        _editingWorkspace = null;
        target?.CommitSourceEdit(edited, isNew);
    }

    public Task ImportDroppedAsync(string path) => (IsLockPage ? Lock : Desktop).ImportPathAsync(path);

    public void RefreshMonitors()
    {
        _screens = _wallpaper.GetMonitors();

        Monitors.Clear();
        foreach (MonitorInfo screen in _screens)
        {
            Monitors.Add(screen);
        }

        Desktop.Reconcile(_screens);
        Lock.Reconcile(_screens);

        OnPropertyChanged(nameof(SupportsPerMonitor));
        OnPropertyChanged(nameof(MonitorSummary));
        OnPropertyChanged(nameof(PerMonitorNotice));
    }

    public async Task RunAsync(string busyText, Func<CancellationToken, Task> work)
    {
        if (Busy)
        {
            return;
        }

        using CancellationTokenSource cts = new();
        _cts = cts;
        Busy = true;
        StatusText = busyText;
        try
        {
            await work(cts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText = "请求已取消。";
        }
        catch (Exception ex)
        {
            StatusText = "失败：" + ex.Message;
            Log(StatusText);
        }
        finally
        {
            Busy = false;
            _cts = null;
        }
    }

    public void Log(string message)
    {
        Activity.Insert(0, DateTime.Now.ToString("MM-dd HH:mm:ss") + "  " + message);
        while (Activity.Count > ActivityLimit)
        {
            Activity.RemoveAt(Activity.Count - 1);
        }
    }

    /// <summary>把每块屏的源列表回写到设置并落盘。</summary>
    public void Save()
    {
        Desktop.SaveData();
        Lock.SaveData();
        Persist();
    }

    /// <summary>只落盘，不再回调工作区，避免保存递归。</summary>
    public void Persist() => _config.Save(Settings);

    public void RaiseCounts()
    {
        OnPropertyChanged(nameof(FavoriteCount));

        // 工作区构造期会回调这里，那时 Desktop/Lock 还没赋值完。
        TargetWorkspace? desktop = Desktop;
        TargetWorkspace? lockScreen = Lock;
        if (desktop is null || lockScreen is null)
        {
            return;
        }

        desktop.RaiseCounts();
        lockScreen.RaiseCounts();

        foreach (NavItem item in NavItems)
        {
            if (item is NavLink link)
            {
                link.Badge = link.Key switch
                {
                    "desktoplib" => $"{desktop.TotalLibraryCount} 张",
                    "locklib" => $"{lockScreen.TotalLibraryCount} 张",
                    "desktopsrc" => $"{desktop.TotalSourceCount} 个",
                    "locksrc" => $"{lockScreen.TotalSourceCount} 个",
                    _ => string.Empty,
                };
            }
        }
    }

    public async Task RefreshAllPreviewsAsync(CancellationToken token)
    {
        await Desktop.RefreshPreviewsAsync(token);
        await Lock.RefreshPreviewsAsync(token);
        EnvironmentReport = BuildEnvironmentReport();
    }

    public void Shutdown()
    {
        _cts?.Cancel();
        Desktop.Stop();
        Lock.Stop();
    }

    private string MigratePage(string? stored)
    {
        string key = string.IsNullOrWhiteSpace(stored) ? "home" : stored;
        return _pageAliases.TryGetValue(key, out string? mapped) ? mapped : key;
    }

    /// <summary>拆分前散在库根目录里的图片，一次性搬进第一块屏的库目录。</summary>
    private void MigrateLooseLibraries()
    {
        if (Settings.LibrarySplitDone)
        {
            return;
        }

        List<(string FromDir, string ToDir)> moves = new();

        foreach (TargetWorkspace target in new[] { Desktop, Lock })
        {
            if (target.Selected is not { } workspace)
            {
                return;
            }

            int moved = LibraryService.MoveLooseFiles(target.RootDir, workspace.LibraryDir);
            if (moved > 0)
            {
                Log($"把 {moved} 张历史壁纸搬进“{workspace.Title}”的壁纸库。");
                workspace.RefreshLibrary();
            }

            moves.Add((target.RootDir, workspace.LibraryDir));

            if (!target.IsLock)
            {
                RepointDesktopWallpaper(target.RootDir, workspace.LibraryDir);
            }
        }

        // 锁屏的 lastApplied 常常记的是桌面那一张，文件搬走后两个目标都要跟着改路径。
        foreach ((string fromDir, string toDir) in moves)
        {
            RetargetMovedPaths(Desktop.Data, fromDir, toDir);
            RetargetMovedPaths(Lock.Data, fromDir, toDir);
        }

        Settings.LibrarySplitDone = true;
        Persist();
    }

    /// <summary>正在显示的那张被搬走了，用新路径重发一次，注册表里就不会留着死路径。</summary>
    public void RepointDesktopWallpaper(string fromDir, string toDir)
    {
        string current = _wallpaper.ReadLegacyWallpaper();
        string prefix = fromDir + Path.DirectorySeparatorChar;

        if (!current.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string candidate = Path.Combine(toDir, Path.GetFileName(current));
        if (File.Exists(candidate))
        {
            _wallpaper.Apply(candidate, Wallpapers.AllMonitors, _wallpaper.ReadStyle());
        }
    }

    /// <summary>搬过家的老路径：注册表还指着我们目录里一个已经不存在的文件，去各屏库里找回同名的那张重发一次。</summary>
    private void RepairMovedWallpaperPath()
    {
        string current = _wallpaper.ReadLegacyWallpaper();
        if (current.Length == 0
            || File.Exists(current)
            || !current.StartsWith(DataPaths.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string name = Path.GetFileName(current);
        try
        {
            foreach (string dir in Directory.GetDirectories(DataPaths.DesktopWallpaperDir))
            {
                string candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                {
                    _wallpaper.Apply(candidate, Wallpapers.AllMonitors, _wallpaper.ReadStyle());
                    Log($"桌面壁纸的旧路径已修到 {candidate}。");
                    return;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>搬完文件后，把记录里指向老根目录的路径改到新位置。</summary>
    private static void RetargetMovedPaths(TargetSettings data, string fromDir, string toDir)
    {
        string prefix = fromDir + Path.DirectorySeparatorChar;

        foreach (MonitorSettings profile in data.Monitors)
        {
            if (!profile.LastApplied.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string candidate = Path.Combine(toDir, Path.GetFileName(profile.LastApplied));
            if (File.Exists(candidate))
            {
                profile.LastApplied = candidate;
            }
        }
    }

    private string BuildEnvironmentReport()
    {
        Version os = Environment.OSVersion.Version;
        List<string> lines = new()
        {
            $"系统版本：Windows {os.Major}.{os.Minor} build {ReadRegistry(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber") ?? os.Build.ToString()}（{os.Revision}）",
            $"运行方式：{(IsPackaged ? "MSIX 包内（数据在 Packages 容器）" : "免安装 / 便携（数据在 LocalAppData）")}",
            $"桌面接口：{EngineText}",
            $"逐显示器：{(SupportsPerMonitor ? "支持（IDesktopWallpaper 已注册）" : "不支持（接口未注册，只能整屏下发）")}",
            $"识别到的屏幕：{MonitorSummary}",
            $"锁屏接口：{LockApiText}",
            $"SkiaSharp：{typeof(SkiaSharp.SKBitmap).Assembly.GetName().Version}",
            $"数据目录：{DataLocation}",
            $"桌面库根目录：{Desktop.RootDir}",
            $"锁屏库根目录：{Lock.RootDir}",
            $"当前桌面壁纸：{CurrentWallpaperText}",
            $"当前锁屏图片：{Lock.Selected?.PreviewPath ?? "未读取到"}",
        };

        foreach (MonitorWorkspace workspace in Desktop.Monitors)
        {
            lines.Add($"  桌面 · {workspace.Label}：{workspace.LibraryCount} 张 → {workspace.LibraryDir}");
        }

        foreach (MonitorWorkspace workspace in Lock.Monitors)
        {
            lines.Add($"  锁屏 · {workspace.Label}：{workspace.LibraryCount} 张 → {workspace.LibraryDir}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string? ReadRegistry(string key, string value)
    {
        try
        {
            return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(key)?
                .GetValue(value) as string;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void OpenInExplorer(string target)
    {
        try
        {
            _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // A missing shell association should not take the app down.
        }
    }

    partial void OnCurrentPageChanged(string value)
    {
        Settings.LastPage = value;
        Persist();
    }

    partial void OnSelectedNavItemChanged(NavItem? value)
    {
        if (value is NavLink link)
        {
            CurrentPage = link.Key;
        }
    }

    partial void OnEncodeChanged(EncodePreference value)
    {
        Settings.Encode = value;
        Persist();
    }

    partial void OnLibraryLimitChanged(int value)
    {
        Settings.LibraryLimit = Math.Clamp(value, 10, 100000);
        Persist();
    }

    partial void OnDesktopStyleChanged(WallpaperStyleMode value)
    {
        Settings.DesktopStyle = value;
        Persist();

        if (Desktop is null)
        {
            return;
        }

        foreach (MonitorWorkspace workspace in Desktop.Monitors)
        {
            workspace.Style = value;
        }

        string label = (StyleOptions.FirstOrDefault(option => option.Key == value).Value ?? value.ToString())
            .Split('（')[0];
        StatusText = $"桌面壁纸填充方式已设为“{label}”，已应用到所有显示器。";
    }

    private bool _syncingStartup;

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_syncingStartup)
        {
            return;
        }

        try
        {
            StartupService.SetEnabled(value);
            Settings.StartWithWindows = value;
            StatusText = value ? "已添加到开机启动，下次登录会自动运行。" : "已取消开机启动。";
            Log(StatusText);
        }
        catch (Exception ex)
        {
            // 注册表被策略锁住时会失败，把开关拨回去，别让界面显示一个没生效的状态。
            _syncingStartup = true;
            StartWithWindows = !value;
            _syncingStartup = false;
            StatusText = "开机启动设置失败：" + ex.Message;
            Log(StatusText);
        }

        Persist();
    }

    private static IReadOnlyList<NavItem> BuildNavItems() => new NavItem[]
    {
        new NavGroup { Label = "浏览" },
        new NavLink { Key = "home", Label = "概览" },
        new NavGroup { Label = "壁纸库" },
        new NavLink { Key = "desktoplib", Label = "桌面壁纸库" },
        new NavLink { Key = "locklib", Label = "锁屏壁纸库" },
        new NavGroup { Label = "壁纸源" },
        new NavLink { Key = "desktopsrc", Label = "桌面壁纸源" },
        new NavLink { Key = "locksrc", Label = "锁屏壁纸源" },
        new NavGroup { Label = "系统" },
        new NavLink { Key = "settings", Label = "设置" },
        new NavLink { Key = "diagnostics", Label = "诊断" },
    };
}
