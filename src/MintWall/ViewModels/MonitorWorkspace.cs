using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using MintWall.Models;
using MintWall.Services;

namespace MintWall.ViewModels;

/// <summary>
/// 一块屏幕在某个目标（桌面或锁屏）下的完整工作区：自己的源列表、自己的库目录、自己的定时与预览。
/// 桌面页与锁屏页顶部的显示器 tab 各自绑定一个实例，分区控件也直接以它为 DataContext。
/// </summary>
public partial class MonitorWorkspace : ObservableObject
{
    private readonly MainViewModel _host;
    private LibraryService _library;
    private readonly ProbeService _probe = new();
    private readonly DispatcherTimer _timer = new();
    private bool _sessionLocked;
    private CancellationTokenSource? _thumbCts;
    private CancellationTokenSource? _sourcePreviewCts;
    private bool _sourcePreviewAttempted;

    public MonitorWorkspace(MainViewModel host, bool isLock, MonitorSettings data)
    {
        _host = host;
        IsLock = isLock;
        Data = data;
        _library = new LibraryService(DataPaths.ResolveLibraryDir(isLock, data));

        Sources.CollectionChanged += Sources_CollectionChanged;
        foreach (SourceProfile source in Data.Sources)
        {
            Sources.Add(source);
        }

        // 构造期直接写后备字段：此时界面还没绑定，避免触发 OnXxxChanged 反过来写盘。
        _selectedSource = Sources.FirstOrDefault(s => s.Id == Data.ActiveSourceId) ?? Sources.FirstOrDefault();

        _style = Data.Style;
        _scheduleEnabled = Data.Schedule.Enabled;
        _scheduleIntervalMinutes = Math.Clamp(Data.Schedule.IntervalMinutes, 1, 43200);
        _fetchFromSources = Data.FetchFromSources;
        _fetchFromLibrary = Data.FetchFromLibrary;
        _fetchFromBuiltIn = Data.FetchFromBuiltIn;

        _timer.Tick += async (_, _) => await FetchAsync(auto: true);

        RefreshLibrary();
        SyncTimer();
        _ = RefreshPreviewAsync(CancellationToken.None);
    }

    public bool IsLock { get; }

    public string TargetTitle => IsLock ? "锁屏" : "桌面";

    /// <summary>锁屏界面不展示“下一次执行时间”，只有桌面目标显示。</summary>
    public bool ShowNextRun => !IsLock;

    public string ScheduleHint => IsLock
        ? "锁屏定时只在会话锁定后开始计时，解锁即暂停，下次锁定重新计时。"
        : "每个目标各有独立定时器，只在程序运行时生效。";

    public MonitorSettings Data { get; }

    public string MonitorId => Data.MonitorId;

    /// <summary>顶栏 tab 与提示语用的名字。</summary>
    public string Label => Data.Label;

    public string ScreenSizes { get; private set; } = string.Empty;

    /// <summary>这块屏的宽高比，预览框照它取高度，图就能整屏显示不被裁掉。</summary>
    public double ScreenAspect { get; private set; } = 16d / 9;

    /// <summary>这块屏能不能真的单独下发；不支持时下发会退化成“所有显示器”。</summary>
    public bool PerMonitorSupported { get; private set; } = true;

    /// <summary>本机不支持逐显示器接口时退化成“所有显示器”，配置仍按这块屏保存。</summary>
    public string ApplyTargetId { get; private set; } = Wallpapers.AllMonitors;

    /// <summary>显示器重新枚举后同步名字、分辨率和实际下发目标。</summary>
    public void AttachScreen(MonitorInfo screen)
    {
        Data.Label = screen.Label;
        ScreenSizes = screen.SizeText;
        ScreenAspect = screen.Width > 0 && screen.Height > 0 ? (double)screen.Width / screen.Height : 16d / 9;
        PerMonitorSupported = screen.PerMonitorSupported;
        ApplyTargetId = screen.ApplyTarget;

        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ScreenSizes));
        OnPropertyChanged(nameof(ScreenAspect));
        OnPropertyChanged(nameof(PerMonitorSupported));
        OnPropertyChanged(nameof(ApplyTargetId));
    }

    public string Title => $"{TargetTitle}·{Label}";

    /// <summary>库目录的来源变了（用户重选、或持久 key 改名）后，把工作区指到新的库目录并重读列表。</summary>
    public void RebuildLibrary()
    {
        _library = new LibraryService(DataPaths.ResolveLibraryDir(IsLock, Data));
        RefreshLibrary();
        OnPropertyChanged(nameof(LibraryDir));
        OnPropertyChanged(nameof(LibraryDirText));
        OnPropertyChanged(nameof(IsBuiltInLibrary));
        OnPropertyChanged(nameof(SaveDirText));
        OnPropertyChanged(nameof(IsBuiltInSave));
    }

    public ObservableCollection<SourceProfile> Sources { get; } = new();

    public ObservableCollection<LibraryItem> Library { get; } = new();

    public ObservableCollection<LibraryItem> RecentItems { get; } = new();

    public string LibraryDir => _library.Directory;

    public IReadOnlyList<KeyValuePair<WallpaperStyleMode, string>> StyleOptions => _host.StyleOptions;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedSource))]
    private SourceProfile? _selectedSource;

    [ObservableProperty]
    private LibraryItem? _selectedLibraryItem;

    [ObservableProperty]
    private bool _favoritesOnly;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private WallpaperStyleMode _style;

    [ObservableProperty]
    private bool _scheduleEnabled;

    [ObservableProperty]
    private int _scheduleIntervalMinutes = 30;

    [ObservableProperty]
    private bool _fetchFromSources = true;

    [ObservableProperty]
    private bool _fetchFromLibrary;

    [ObservableProperty]
    private bool _fetchFromBuiltIn;

    [ObservableProperty]
    private string _nextRunText = "未启用";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    private ImageSource? _preview;

    [ObservableProperty]
    private string _previewPath = "未设置";

    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSourcePreview))]
    private ImageSource? _sourcePreview;

    [ObservableProperty]
    private string _sourcePreviewStatus = "选一个源查看预览";

    [ObservableProperty]
    private string _sourcePreviewDetail = string.Empty;

    public bool HasSelectedSource => SelectedSource is not null;

    public bool HasPreview => Preview is not null;

    public bool HasSourcePreview => SourcePreview is not null;

    public bool CanUseOwnSource => Sources.Any(source => source.Enabled);

    public int SourceCount => Sources.Count;

    public int EnabledSourceCount => Sources.Count(source => source.Enabled);

    public int LibraryCount => Library.Count;

    /// <summary>有没有能用的换图来源（内置始终算能用）。</summary>
    public bool HasFetchChannel =>
        FetchFromBuiltIn || (FetchFromLibrary && Library.Count > 0) || (FetchFromSources && EnabledSourceCount > 0);

    [RelayCommand]
    private async Task FetchAsync() => await FetchAsync(auto: false);

    [RelayCommand]
    public async Task ImportLocalAsync()
    {
        OpenFileDialog dialog = new()
        {
            Title = IsLock ? $"选择{Label}的锁屏图片（仅支持 JPG / PNG）" : $"选择要导入{Label}壁纸库的图片",
            Filter = IsLock ? "图片|*.jpg;*.jpeg;*.png|所有文件|*.*" : "图片|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp|所有文件|*.*",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await _host.RunAsync($"正在导入 {Path.GetFileName(dialog.FileName)}…", async token =>
        {
            AcquiredImage acquired = await Task.Run(() => _library.ImportLocal(dialog.FileName, _host.Encode), token);
            await ApplyPathAsync(acquired.FilePath, "已导入并应用", token);
        });
    }

    [RelayCommand]
    private async Task TestSourceAsync()
    {
        if (SelectedSource is not { } source)
        {
            return;
        }

        await _host.RunAsync($"正在测试「{source.Name}」…", async token =>
        {
            ProbeResult result = await _probe.ProbeAsync(source, _host.Encode, token);
            _host.ProbeReport = result.Report;

            if (result.ImageBytes is { Length: > 0 } bytes)
            {
                await ShowSourcePreviewAsync(source, bytes, result.Width, result.Height, token);
                _host.StatusText = $"「{source.Name}」测试完成，预览已更新，详见“诊断”页。";
            }
            else
            {
                SourcePreview = null;
                SourcePreviewDetail = string.Empty;
                SourcePreviewStatus = "测试没取到可预览的图片，原因见“诊断”页";
                _host.StatusText = $"「{source.Name}」测试完成，但没取到图片，详见“诊断”页。";
            }

            _host.Log($"测试接口：{source.Name}（{Title}）");
        });
    }

    /// <summary>打开壁纸源页面时补一张预览，启动阶段不提前占用网络。</summary>
    public void EnsureSourcePreview()
    {
        if (!_sourcePreviewAttempted && SelectedSource is { } source)
        {
            _ = LoadSourcePreviewAsync(source, quiet: true);
        }
    }

    [RelayCommand]
    private Task RefreshSourcePreviewAsync() =>
        SelectedSource is { } source ? LoadSourcePreviewAsync(source, quiet: false) : Task.CompletedTask;

    /// <summary>取一张该源的样片放进右边的预览框；样片只留在缓存，不写入壁纸库。</summary>
    private async Task LoadSourcePreviewAsync(SourceProfile source, bool quiet)
    {
        _sourcePreviewAttempted = true;
        _sourcePreviewCts?.Cancel();
        CancellationTokenSource cts = new();
        _sourcePreviewCts = cts;

        if (quiet && !source.Enabled)
        {
            SourcePreview = null;
            SourcePreviewDetail = string.Empty;
            SourcePreviewStatus = "该源已停用，点“刷新预览”仍可临时取一张看看";
            return;
        }

        SourcePreview = null;
        SourcePreviewDetail = string.Empty;
        SourcePreviewStatus = $"正在从「{source.Name}」取一张预览…";

        try
        {
            string dir = Path.Combine(DataPaths.CacheDirFor(IsLock), "preview");
            AcquiredImage acquired = await ImageService.AcquireAsync(source, dir, EncodePreference.Jpeg, cts.Token);
            ImageSource? image = await Task.Run(() => ThumbnailStore.TryLoad(acquired.FilePath, 900), cts.Token);
            TryDelete(acquired.FilePath);

            if (cts.IsCancellationRequested || !ReferenceEquals(SelectedSource, source))
            {
                return;
            }

            SourcePreview = image;
            SourcePreviewStatus = image is null ? "取到了图片，但预览解码失败" : string.Empty;
            SourcePreviewDetail = $"{acquired.Width} × {acquired.Height} · {ImageService.FormatSize(acquired.Bytes)}";
        }
        catch (OperationCanceledException)
        {
            // 换选了别的源，这次预览作废。
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested && ReferenceEquals(SelectedSource, source))
            {
                SourcePreviewStatus = "预览失败：" + ex.Message;
            }
        }
    }

    /// <summary>把“测试”刚取到的图直接放进预览框，不重新请求一次。</summary>
    private async Task ShowSourcePreviewAsync(SourceProfile source, byte[] body, int width, int height, CancellationToken token)
    {
        _sourcePreviewCts?.Cancel();
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        _sourcePreviewCts = cts;

        string file = Path.Combine(DataPaths.CacheDirFor(IsLock), "preview", $"{Guid.NewGuid():N}.img");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllBytesAsync(file, body, cts.Token);
            ImageSource? image = await Task.Run(() => ThumbnailStore.TryLoad(file, 900), cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(SelectedSource, source))
            {
                return;
            }

            SourcePreview = image;
            SourcePreviewStatus = image is null ? "取到了图片，但预览解码失败" : string.Empty;
            SourcePreviewDetail = $"{width} × {height} · {ImageService.FormatSize(body.Length)}";
        }
        finally
        {
            TryDelete(file);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [RelayCommand]
    private void AddSource() =>
        _host.RequestEditSource(new SourceProfile { Name = "新的壁纸源", Url = SelectedSource?.Url ?? "https://t.alcy.cc/ycy" }, true, this);

    [RelayCommand]
    private void EditSource()
    {
        if (SelectedSource is { } source)
        {
            _host.RequestEditSource(source.Clone(), false, this);
        }
    }

    [RelayCommand]
    private void RemoveSource()
    {
        if (SelectedSource is not { } source)
        {
            return;
        }

        Sources.Remove(source);
        SelectedSource = Sources.FirstOrDefault();
        _host.StatusText = $"已删除{Title}的壁纸源“{source.Name}”。";
        _host.Log($"删除壁纸源“{source.Name}”（{Title}）。");
        RaiseCounts();
        Save();
    }

    /// <summary>锁屏页专用：把同一块屏在桌面那一侧的源列表复制过来，省去重复录入。</summary>
    [RelayCommand]
    private void CopySourcesFromDesktop() => CopySourcesFrom(_host.Desktop, "桌面");

    [RelayCommand]
    private void CopySourcesFromLock() => CopySourcesFrom(_host.Lock, "锁屏");

    private void CopySourcesFrom(TargetWorkspace target, string label)
    {
        MonitorWorkspace? donor = target.ForMonitor(MonitorId) ?? target.Selected;
        if (donor is null || donor.Sources.Count == 0)
        {
            _host.StatusText = $"{label}还没有可复制的壁纸源。";
            return;
        }

        int added = 0;
        int skipped = 0;
        foreach (SourceProfile source in donor.Sources)
        {
            if (Sources.Any(existing => IsSameSource(existing, source)))
            {
                skipped++;
                continue;
            }

            Sources.Add(source.Clone());
            added++;
        }

        if (added == 0)
        {
            _host.StatusText = $"{label}里的源都已经在{Title}里了，没有新增。";
            return;
        }

        SelectedSource = Sources[^1];
        string skipNote = skipped > 0 ? $"，跳过 {skipped} 个重复项" : string.Empty;
        _host.StatusText = $"已把{donor.Title}的 {added} 个源复制到{Title}{skipNote}，之后两边互不影响。";
        _host.Log($"复制源：{donor.Title} → {Title}（新增 {added}，重复 {skipped}）");
        RaiseCounts();
        Save();
    }

    private static bool IsSameSource(SourceProfile left, SourceProfile right)
    {
        string leftUrl = left.Url.Trim();
        string rightUrl = right.Url.Trim();
        if (leftUrl.Length > 0 || rightUrl.Length > 0)
        {
            return string.Equals(leftUrl, rightUrl, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(left.Name.Trim(), right.Name.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private void ApplyItem(LibraryItem? item)
    {
        if (item is not null)
        {
            _ = _host.RunAsync("正在应用壁纸…", async token => await ApplyPathAsync(item.FilePath, "已应用", token));
        }
    }

    [RelayCommand]
    private void ToggleFavoriteItem(LibraryItem? item)
    {
        if (item is not null)
        {
            SetFavorite(item);
        }
    }

    [RelayCommand]
    private void DeleteItem(LibraryItem? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            _library.Delete(item.FilePath);
            _host.Settings.Favorites.Remove(item.Id);
            Library.Remove(item);
            RecentItems.Remove(item);
            RaiseCounts();
            Save();
            _host.StatusText = $"已删除 {item.FileName}";
            _host.Log($"删除 {item.FileName}（{Title}）");
        }
        catch (IOException ex)
        {
            _host.StatusText = "删除失败：" + ex.Message;
        }
    }

    [RelayCommand]
    public void OpenLibraryFolder() => _library.OpenFolder();

    /// <summary>壁纸库改成用户自己挑的文件夹。</summary>
    [RelayCommand]
    private void ChooseLibraryFolder()
    {
        OpenFolderDialog dialog = new()
        {
            Title = $"选择“{Title}”的壁纸库目录",
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        SetLibraryDirectory(dialog.FolderName);
    }

    /// <summary>改回应用内置的按屏目录。</summary>
    [RelayCommand]
    private void ResetLibraryFolder() => SetLibraryDirectory(string.Empty);

    public bool IsBuiltInLibrary => string.IsNullOrEmpty(Data.LibraryDirectory);

    /// <summary>当前这块屏真正在用的壁纸库目录。</summary>
    public string LibraryDirText => _library.Directory;

    public void SetLibraryDirectory(string directory)
    {
        if (string.Equals(Data.LibraryDirectory ?? string.Empty, directory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Data.LibraryDirectory = directory;
        RebuildLibrary();
        Save();

        _host.StatusText = IsBuiltInLibrary
            ? $"“{Title}”的壁纸库回到内置目录：{LibraryDirText}"
            : $"“{Title}”的壁纸库改到 {LibraryDirText}，子文件夹一起收进来，程序不会自动清理这里的图。";
        _host.Log(_host.StatusText);
    }

    /// <summary>自动保存的图落进这块屏自己的保存位置，和壁纸库分开，不会被库里筛选和清理波及。</summary>
    [RelayCommand]
    private void ChooseSaveFolder()
    {
        OpenFolderDialog dialog = new()
        {
            Title = $"选择“{Title}”的保存位置",
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        SetSaveDirectory(dialog.FolderName);
    }

    [RelayCommand]
    private void ResetSaveFolder() => SetSaveDirectory(string.Empty);

    [RelayCommand]
    public void OpenSaveFolder() => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{SaveDirText}\"") { UseShellExecute = true });

    /// <summary>概览页点预览图用：在资源管理器里选中当前这张图所在的位置。</summary>
    [RelayCommand]
    private void RevealCurrentImage()
    {
        if (!File.Exists(PreviewPath))
        {
            _host.StatusText = "当前图片文件不存在，可能已被系统或其他程序替换。";
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{PreviewPath}\"") { UseShellExecute = true });
    }

    public bool IsBuiltInSave => string.IsNullOrEmpty(Data.SaveDirectory);

    /// <summary>当前这块屏真正在用的保存位置。</summary>
    public string SaveDirText => DataPaths.ResolveSaveDir(IsLock, Data);

    public void SetSaveDirectory(string directory)
    {
        if (string.Equals(Data.SaveDirectory ?? string.Empty, directory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Data.SaveDirectory = directory;
        Save();
        OnPropertyChanged(nameof(SaveDirText));
        OnPropertyChanged(nameof(IsBuiltInSave));

        _host.StatusText = IsBuiltInSave
            ? $"“{Title}”的保存位置回到内置目录：{SaveDirText}"
            : $"“{Title}”的保存位置改到 {SaveDirText}，开了自动保存的源抓到的图都存在这里，不会进壁纸库列表。";
        _host.Log(_host.StatusText);
    }

    [RelayCommand]
    public void Refresh()
    {
        RefreshLibrary();
        _ = RefreshPreviewAsync(CancellationToken.None);
    }

    /// <summary>拖放进窗口的图片直接导入当前这块屏的库。</summary>
    public Task ImportPathAsync(string path) =>
        _host.RunAsync($"正在导入 {Path.GetFileName(path)}…", async token =>
        {
            AcquiredImage acquired = await Task.Run(() => _library.ImportLocal(path, _host.Encode), token);
            await ApplyPathAsync(acquired.FilePath, "已导入并应用", token);
        });

    /// <summary>抓一张图：在勾选的内置 / 壁纸库 / 壁纸源里随机挑一路，抓到的图按源的自动保存设置落盘。</summary>
    public async Task FetchAsync(bool auto)
    {
        FetchChannel channel = ResolveChannel();

        if (channel == FetchChannel.Library)
        {
            LibraryItem? item = NextLibraryItem();
            if (item is not null)
            {
                await _host.RunAsync($"正在从{Title}壁纸库换图…", async token =>
                    await ApplyPathAsync(item.FilePath, auto ? $"定时已换{Title}壁纸" : $"已换{Title}壁纸", token));
                return;
            }

            // 库里没有能用的图（例如锁屏库全是 webp），退到内置图。
            channel = FetchChannel.BuiltIn;
        }

        SourceProfile? picked = channel == FetchChannel.Sources ? ResolveRandomSource() : null;
        if (channel == FetchChannel.Sources && picked is null)
        {
            // 启用的源刚好被停用了，退回内置必应。
            channel = FetchChannel.BuiltIn;
        }

        SourceProfile source = picked ?? BingService.BuiltInSource();
        string from = channel == FetchChannel.BuiltIn ? "必应每日壁纸" : $"「{source.Name}」";

        await _host.RunAsync($"正在为{Title}从{from}获取…", async token =>
        {
            bool keep = source.AutoSave;
            string targetDir = keep ? SaveDirText : DataPaths.CacheDirFor(IsLock);
            AcquiredImage acquired = await ImageService.AcquireAsync(source, targetDir, _host.Encode, token);
            _library.Record(acquired);
            await ApplyPathAsync(acquired.FilePath, auto ? $"定时已换{Title}壁纸" : $"已换{Title}壁纸", token);

            if (!keep)
            {
                _library.PruneCache(targetDir, acquired.FilePath, _host.Settings.Favorites);
            }
        });
    }

    /// <summary>把一张已入库的图片应用到这块屏；桌面还会按开关顺带写锁屏。</summary>
    public async Task ApplyPathAsync(string imagePath, string verb, CancellationToken token)
    {
        if (IsLock)
        {
            LockScreenOutcome outcome = await LockScreenService.ApplyAsync(imagePath, token);
            Data.LastApplied = outcome.AppliedPath ?? imagePath;
            Message = outcome.Success ? string.Empty : outcome.Message;
            Save();
            RefreshLibrary();
            await RefreshPreviewAsync(token);

            _host.StatusText = outcome.Success
                ? $"锁屏已更新（{Label}）：{Path.GetFileName(imagePath)}"
                : "锁屏设置失败：" + outcome.Message;
            _host.Log(_host.StatusText);
            return;
        }

        _host.Wallpaper.Apply(imagePath, ApplyTargetId, Style);
        _host.RefreshMonitors();
        _host.CurrentWallpaperText = _host.Wallpaper.ReadLegacyWallpaper();
        Data.LastApplied = imagePath;
        RefreshLibrary();

        string lockNote = _host.Desktop.SyncLockScreen ? await _host.Lock.SyncFromDesktopAsync(imagePath, MonitorId, token) : string.Empty;
        await RefreshPreviewAsync(token);

        string detail = $"{Path.GetFileName(imagePath)} → {Label}";
        _host.StatusText = $"{verb}：{detail}{lockNote}";
        _host.Log($"{Title} ← {Path.GetFileName(imagePath)}{lockNote}");
    }

    /// <summary>桌面同步锁屏时复用，返回一句追加到状态栏的说明。</summary>
    public async Task<string> SyncLockNoteAsync(string imagePath, CancellationToken token)
    {
        LockScreenOutcome outcome = await LockScreenService.ApplyAsync(imagePath, token);
        Data.LastApplied = outcome.AppliedPath ?? imagePath;
        Message = outcome.Success ? string.Empty : outcome.Message;
        Save();
        await RefreshPreviewAsync(token);
        return outcome.Success ? " · 锁屏已同步" : " · 锁屏未同步（" + outcome.Message + "）";
    }

    public void CommitSourceEdit(SourceProfile edited, bool isNew)
    {
        if (isNew)
        {
            Sources.Add(edited);
        }
        else
        {
            SourceProfile? existing = Sources.FirstOrDefault(s => s.Id == edited.Id);
            if (existing is null)
            {
                return;
            }

            existing.Name = edited.Name;
            existing.Url = edited.Url;
            existing.Kind = edited.Kind;
            existing.JsonFieldPath = edited.JsonFieldPath;
            existing.Headers = edited.Headers;
            existing.AutoSave = edited.AutoSave;
            existing.Enabled = edited.Enabled;
        }

        SelectedSource = Sources.FirstOrDefault(s => s.Id == edited.Id);
        if (SelectedSource is { } saved)
        {
            _ = LoadSourcePreviewAsync(saved, quiet: true);
        }
        _host.StatusText = $"已{(isNew ? "添加" : "更新")}{Title}的壁纸源“{edited.Name}”，建议先到“诊断”测一次。";
        _host.Log($"{(isNew ? "新增" : "更新")}{Title}壁纸源“{edited.Name}”。");
        RaiseCounts();
        Save();
    }

    public void SetFavorite(LibraryItem item)
    {
        if (item.IsFavorite)
        {
            _host.Settings.Favorites.Remove(item.Id);
            item.IsFavorite = false;
        }
        else
        {
            _host.Settings.Favorites.Add(item.Id);
            item.IsFavorite = true;
        }

        RaiseCounts();
        Save();
        _host.StatusText = $"{item.FileName} {(item.IsFavorite ? "已收藏" : "取消收藏")}";
        if (FavoritesOnly && !item.IsFavorite)
        {
            Library.Remove(item);
        }
    }

    public void RefreshLibrary()
    {
        string[] favorites = _host.Settings.Favorites.ToArray();
        int removed = _library.Trim(favorites, _host.Settings.LibraryLimit);
        List<string> files = _library.ListFiles().ToList();
        string needle = SearchText.Trim();

        Library.Clear();
        foreach (string path in files)
        {
            if (FavoritesOnly && !favorites.Contains(Path.GetFileNameWithoutExtension(path)))
            {
                continue;
            }

            if (needle.Length > 0 && !Path.GetFileName(path).Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Library.Add(_library.ToItem(path, favorites));
        }

        RecentItems.Clear();
        foreach (LibraryItem item in Library.Take(8))
        {
            RecentItems.Add(item);
        }

        RaiseCounts();

        if (removed > 0)
        {
            _host.StatusText = $"{Title}壁纸库超出 {_host.Settings.LibraryLimit} 张，已清理 {removed} 张较早的（收藏不删）。";
        }

        _ = LoadThumbnailsAsync(Library.Take(150).ToList());
    }

    public async Task RefreshPreviewAsync(CancellationToken token)
    {
        string? path = IsLock ? LockScreenService.ReadCurrentPath() : _host.Wallpaper.CurrentWallpaperOf(MonitorId);

        if (IsLock)
        {
            PreviewPath = path ?? "未读取到（可能被聚焦或策略接管）";
        }
        else
        {
            PreviewPath = string.IsNullOrWhiteSpace(path) ? "未设置" : path;
        }

        try
        {
            ImageSource? image = path is null || !File.Exists(path)
                ? null
                : await Task.Run(() => ThumbnailStore.TryLoad(path, ThumbnailStore.PreviewEdge), token);

            Preview = image;
        }
        catch (OperationCanceledException)
        {
            // 有更新的刷新胜出了。
        }
    }

    public void StartSchedule() => SyncTimer();

    /// <summary>锁屏定时从会话锁定那一刻开始计时；解锁立即停表，等下次锁定重新开始。</summary>
    public void NotifySessionLocked(bool locked)
    {
        if (!IsLock || _sessionLocked == locked)
        {
            return;
        }

        _sessionLocked = locked;
        SyncTimer();
    }

    public void Stop()
    {
        _thumbCts?.Cancel();
        _sourcePreviewCts?.Cancel();
        _timer.Stop();
    }

    public void SaveData()
    {
        Data.Sources = Sources.ToList();
        Data.ActiveSourceId = SelectedSource?.Id;
    }

    public void Save()
    {
        SaveData();
        _host.Persist();
    }

    private async Task LoadThumbnailsAsync(IReadOnlyList<LibraryItem> items)
    {
        _thumbCts?.Cancel();
        CancellationTokenSource cts = new();
        _thumbCts = cts;

        foreach (LibraryItem item in items)
        {
            ImageSource? image = null;
            try
            {
                image = await Task.Run(() => ThumbnailStore.TryLoad(item.FilePath), cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (cts.IsCancellationRequested)
            {
                return;
            }

            item.Thumbnail = image;
        }
    }

    private enum FetchChannel
    {
        BuiltIn,
        Library,
        Sources,
    }

    /// <summary>勾选的来源里随机挑一个能用的；每次都随机，不按顺序。</summary>
    private FetchChannel ResolveChannel()
    {
        bool hasEnabledSources = EnabledSourceCount > 0;
        List<FetchChannel> enabled = new(3);
        if (FetchFromBuiltIn)
        {
            enabled.Add(FetchChannel.BuiltIn);
        }

        if (FetchFromLibrary && Library.Count > 0)
        {
            enabled.Add(FetchChannel.Library);
        }

        if (FetchFromSources && hasEnabledSources)
        {
            enabled.Add(FetchChannel.Sources);
        }

        if (enabled.Count == 0)
        {
            return hasEnabledSources ? FetchChannel.Sources : FetchChannel.BuiltIn;
        }

        return enabled[Random.Shared.Next(enabled.Count)];
    }

    private LibraryItem? NextLibraryItem()
    {
        List<LibraryItem> pool = Library
            .Where(item => !IsLock || Path.GetExtension(item.FilePath).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png")
            .ToList();

        if (pool.Count > 1)
        {
            pool.RemoveAll(item => string.Equals(item.FilePath, PreviewPath, StringComparison.OrdinalIgnoreCase));
        }

        if (pool.Count == 0)
        {
            return null;
        }

        return pool[Random.Shared.Next(pool.Count)];
    }

    /// <summary>启用的壁纸源里随机挑一个，尽量不和当前选中的那个重复。</summary>
    private SourceProfile? ResolveRandomSource()
    {
        List<SourceProfile> usable = Sources.Where(source => source.Enabled).ToList();
        if (usable.Count == 0)
        {
            return null;
        }

        if (usable.Count == 1)
        {
            return usable[0];
        }

        List<SourceProfile> pool = usable
            .Where(source => !ReferenceEquals(source, SelectedSource))
            .ToList();
        SelectedSource = pool[Random.Shared.Next(pool.Count)];
        return SelectedSource;
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(SourceCount));
        OnPropertyChanged(nameof(EnabledSourceCount));
        OnPropertyChanged(nameof(LibraryCount));
        OnPropertyChanged(nameof(CanUseOwnSource));
        OnPropertyChanged(nameof(HasFetchChannel));
        _host.RaiseCounts();
    }

    private void Sources_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (SourceProfile source in e.NewItems)
            {
                source.PropertyChanged += Source_PropertyChanged;
            }
        }

        if (e.OldItems is not null)
        {
            foreach (SourceProfile source in e.OldItems)
            {
                source.PropertyChanged -= Source_PropertyChanged;
            }
        }
    }

    private void Source_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SourceProfile.Enabled) || sender is not SourceProfile source)
        {
            return;
        }

        RaiseCounts();
        SyncTimer();
        Save();
        _host.StatusText = $"{Title}的源“{source.Name}”已{(source.Enabled ? "启用" : "停用")}。";
        _host.Log(_host.StatusText);

        if (ReferenceEquals(source, SelectedSource))
        {
            _ = LoadSourcePreviewAsync(source, quiet: true);
        }
    }

    private void SyncTimer()
    {
        bool counting = !IsLock || _sessionLocked;

        if (ScheduleEnabled && HasFetchChannel && counting)
        {
            _timer.Interval = TimeSpan.FromMinutes(Math.Clamp(ScheduleIntervalMinutes, 1, 43200));
            _timer.Start();
            NextRunText = "下次 " + DateTime.Now.Add(_timer.Interval).ToString("MM-dd HH:mm");
        }
        else
        {
            _timer.Stop();

            if (!ScheduleEnabled)
            {
                NextRunText = "未启用";
            }
            else if (!HasFetchChannel)
            {
                NextRunText = "没有可用的换图来源，定时器未启动";
            }
            else
            {
                NextRunText = "锁定后开始计时";
            }
        }
    }

    partial void OnSelectedSourceChanged(SourceProfile? value)
    {
        if (value is not null)
        {
            Data.ActiveSourceId = value.Id;
            RaiseCounts();
            Save();
            _ = LoadSourcePreviewAsync(value, quiet: true);
        }
        else
        {
            _sourcePreviewCts?.Cancel();
            SourcePreview = null;
            SourcePreviewDetail = string.Empty;
            SourcePreviewStatus = "选一个源查看预览";
        }
    }

    partial void OnStyleChanged(WallpaperStyleMode value)
    {
        Data.Style = value;
        Save();
    }

    partial void OnFavoritesOnlyChanged(bool value) => RefreshLibrary();

    partial void OnSearchTextChanged(string value) => RefreshLibrary();

    partial void OnScheduleEnabledChanged(bool value) => ApplySchedule();

    partial void OnScheduleIntervalMinutesChanged(int value) => ApplySchedule();

    partial void OnFetchFromSourcesChanged(bool value) => ApplyFetchChannels(nameof(FetchFromSources));

    partial void OnFetchFromLibraryChanged(bool value) => ApplyFetchChannels(nameof(FetchFromLibrary));

    partial void OnFetchFromBuiltInChanged(bool value) => ApplyFetchChannels(nameof(FetchFromBuiltIn));

    private bool _syncingChannels;

    /// <summary>三个换图来源可以多选；全关时把刚关掉的那个拨回去，保证始终有一路可用。</summary>
    private void ApplyFetchChannels(string changed)
    {
        if (_syncingChannels)
        {
            return;
        }

        bool bounced = false;

        if (!FetchFromSources && !FetchFromLibrary && !FetchFromBuiltIn)
        {
            bounced = true;
            _syncingChannels = true;
            switch (changed)
            {
                case nameof(FetchFromSources):
                    FetchFromSources = true;
                    break;
                case nameof(FetchFromLibrary):
                    FetchFromLibrary = true;
                    break;
                default:
                    FetchFromBuiltIn = true;
                    break;
            }
            _syncingChannels = false;
        }

        Data.FetchFromSources = FetchFromSources;
        Data.FetchFromLibrary = FetchFromLibrary;
        Data.FetchFromBuiltIn = FetchFromBuiltIn;
        Save();
        SyncTimer();

        List<string> names = new(3);
        if (FetchFromBuiltIn)
        {
            names.Add("内置");
        }

        if (FetchFromLibrary)
        {
            names.Add("壁纸库");
        }

        if (FetchFromSources)
        {
            names.Add("壁纸源");
        }

        _host.StatusText = bounced
            ? "换图来源至少要留一个，刚才那个没关掉。"
            : $"{Title}的换图来源：{string.Join(" + ", names)}。";
    }

    private void ApplySchedule()
    {
        Data.Schedule.Enabled = ScheduleEnabled;
        Data.Schedule.IntervalMinutes = Math.Clamp(ScheduleIntervalMinutes, 1, 43200);
        Save();
        SyncTimer();
        _host.StatusText = ScheduleEnabled
            ? $"{Title}定时换壁纸已开启：每 {Data.Schedule.IntervalMinutes} 分钟一次，在勾选的来源里随机换一张。"
            : $"{Title}定时换壁纸已关闭。";
    }
}
