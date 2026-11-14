using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using MintWall.Models;
using MintWall.Services;

namespace MintWall.ViewModels;

/// <summary>
/// 一块屏的视频壁纸库：本地视频 + 在线源。版式与桌面壁纸库/壁纸源一致，
/// 顶栏 tab 切到哪块屏就操作哪块屏的库；播放交给独立的视频宿主进程。
/// </summary>
public partial class VideoSectionWorkspace : ObservableObject
{
    private readonly MainViewModel _host;
    private readonly MonitorWorkspace _monitor;
    private readonly VideoLibrarySettings _data;
    private readonly HashSet<string> _favorites;
    private CancellationTokenSource? _thumbCts;
    private CancellationTokenSource? _scanCts;

    public VideoSectionWorkspace(MainViewModel host, MonitorWorkspace monitor, VideoLibrarySettings data)
    {
        _host = host;
        _monitor = monitor;
        _data = data;
        _favorites = new HashSet<string>(data.FavoritePaths, StringComparer.OrdinalIgnoreCase);

        foreach (VideoSource source in data.Sources)
        {
            Subscribe(source);
            Sources.Add(source);
        }

        // 构造期直接写后备字段，避免触发 OnXxxChanged 反向写盘。
        _localEnabled = data.LocalEnabled;
        _onlineEnabled = data.OnlineEnabled;
        _selectedSource = Sources.FirstOrDefault();
        _currentVideoText = string.IsNullOrWhiteSpace(data.LastPlayed)
            ? "未播放"
            : Path.GetFileName(data.LastPlayed);

        Rescan();
    }

    public ObservableCollection<VideoItem> LocalVideos { get; } = new();

    /// <summary>按搜索词过滤后的视图，绑定到画廊。</summary>
    public ObservableCollection<VideoItem> View { get; } = new();

    public ObservableCollection<VideoSource> Sources { get; } = new();

    [ObservableProperty]
    private VideoItem? _selectedLocalVideo;

    [ObservableProperty]
    private VideoSource? _selectedSource;

    [ObservableProperty]
    private bool _localEnabled;

    [ObservableProperty]
    private bool _onlineEnabled;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _favoritesOnly;

    [ObservableProperty]
    private string _statusText = "未播放视频壁纸。";

    [ObservableProperty]
    private string _currentVideoText = "未播放";

    /// <summary>在线源预览框里正在预览的视频；null 表示还没预览过。</summary>
    [ObservableProperty]
    private Uri? _previewSource;

    public bool HasPreview => PreviewSource is not null;

    /// <summary>区块标题里的目标+屏名（与“壁纸库 · 桌面·显示器 1”同款）。</summary>
    public string Title => _monitor.Title;

    /// <summary>这块屏真正在用的视频库目录：用户选过就用用户的，否则用内置按屏目录。</summary>
    public string LibraryDirText => string.IsNullOrWhiteSpace(_data.LibraryDirectory)
        ? DataPaths.VideoDirFor(_monitor.MonitorId)
        : _data.LibraryDirectory;

    public bool IsBuiltInLibrary => string.IsNullOrWhiteSpace(_data.LibraryDirectory);

    /// <summary>自动保存的保存位置：用户选过就用用户的，否则用内置按屏归档目录。</summary>
    public string SaveDirText => string.IsNullOrWhiteSpace(_data.SaveDirectory)
        ? DataPaths.VideoSaveDirFor(_monitor.MonitorId)
        : _data.SaveDirectory;

    /// <summary>选中的源实际用的保存位置：没指定自定义目录时按源名建子文件夹。</summary>
    public string SelectedSaveDirText => SaveDirForSource(SelectedSource);

    public bool IsBuiltInSave => string.IsNullOrWhiteSpace(_data.SaveDirectory);

    /// <summary>每个在线源的默认归档目录：内置根下按源名再分一层，避免互相覆盖。</summary>
    private string SaveDirForSource(VideoSource? source)
    {
        if (!string.IsNullOrWhiteSpace(_data.SaveDirectory))
        {
            return _data.SaveDirectory;
        }

        string baseDir = DataPaths.VideoSaveDirFor(_monitor.MonitorId);
        if (source is null || string.IsNullOrWhiteSpace(source.Name))
        {
            return baseDir;
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        string safe = string.Concat(source.Name.Trim().Select(ch => invalid.Contains(ch) ? '_' : ch)).Trim();
        return safe.Length == 0 ? baseDir : Path.Combine(baseDir, safe);
    }

    public int LocalCount => LocalVideos.Count;

    public int FilteredCount => View.Count;

    public int SourceCount => Sources.Count;

    public int EnabledSourceCount => Sources.Count(source => source.Enabled);

    public bool HasLocalVideos => LocalVideos.Count > 0;

    public bool HasSources => Sources.Count > 0;

    public bool HasSelectedSource => SelectedSource is not null;

    /// <summary>本地库或在线库有可用内容；概览页「视频」来源靠它决定能不能选中。</summary>
    public bool HasContent => (LocalEnabled && LocalVideos.Count > 0) || (OnlineEnabled && EnabledSourceCount > 0);

    public string LocalSummary => LocalEnabled ? $"{LocalVideos.Count} 个视频" : "本地库已停用";

    public string OnlineSummary => OnlineEnabled ? $"{EnabledSourceCount} 个启用源" : "在线库已停用";

    public IReadOnlyList<KeyValuePair<SourceKind, string>> KindOptions { get; } = new[]
    {
        new KeyValuePair<SourceKind, string>(SourceKind.Auto, "自动（直链或 JSON）"),
        new KeyValuePair<SourceKind, string>(SourceKind.DirectImage, "直链视频"),
        new KeyValuePair<SourceKind, string>(SourceKind.Json, "JSON 接口"),
    };

    [RelayCommand]
    private async Task ImportLocalVideosAsync()
    {
        OpenFileDialog dialog = new()
        {
            Title = $"导入本地视频（{_monitor.Label}）",
            Filter = "视频文件|*.mp4;*.wmv;*.avi;*.mov;*.m4v|所有文件|*.*",
            Multiselect = true,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        // 复制大视频可能要几十秒，别把 UI 线程钉死。
        string[] files = dialog.FileNames;
        string targetDir = LibraryDirText;
        int added = await _host.RunAsync($"正在导入视频到 {Title}…", async token => await Task.Run(() =>
        {
            int count = 0;
            foreach (string path in files)
            {
                try
                {
                    VideoLibraryStore.ImportLocal(path, targetDir);
                    count++;
                }
                catch (Exception ex)
                {
                    _host.Log($"导入视频失败（{Path.GetFileName(path)}）：{ex.Message}");
                }
            }

            return count;
        }, token));

        Rescan();
        SelectedLocalVideo = View.LastOrDefault();
        SyncData();
        StatusText = added > 0 ? $"已导入 {added} 个视频。" : "没有导入任何视频。";
    }

    /// <summary>视频库改成用户自己挑的文件夹（含子文件夹）。</summary>
    [RelayCommand]
    private void ChooseLibraryFolder()
    {
        OpenFolderDialog dialog = new()
        {
            Title = $"选择“{Title}”的视频库目录",
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

    [RelayCommand]
    private void RefreshLibrary()
    {
        Rescan();
        StatusText = $"已刷新：{LocalCount} 个视频。";
    }

    /// <summary>自动保存的视频存到哪里（选目录 / 恢复内置 / 打开）。</summary>
    [RelayCommand]
    private void ChooseSaveFolder()
    {
        OpenFolderDialog dialog = new()
        {
            Title = $"选择“{Title}”的视频保存位置",
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
    private void OpenSaveFolder() =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{SaveDirText}\"")
        {
            UseShellExecute = true,
        });

    private void SetSaveDirectory(string directory)
    {
        if (string.Equals(_data.SaveDirectory ?? string.Empty, directory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _data.SaveDirectory = directory;
        OnPropertyChanged(nameof(SaveDirText));
        OnPropertyChanged(nameof(SelectedSaveDirText));
        OnPropertyChanged(nameof(IsBuiltInSave));
        _host.Persist();
        _host.Log(IsBuiltInSave
            ? $"“{Title}”的视频保存位置回到内置目录：{SaveDirText}"
            : $"“{Title}”的视频保存位置改到 {SaveDirText}。");
    }

    private void SetLibraryDirectory(string directory)
    {
        if (string.Equals(_data.LibraryDirectory ?? string.Empty, directory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _data.LibraryDirectory = directory;
        OnPropertyChanged(nameof(LibraryDirText));
        OnPropertyChanged(nameof(IsBuiltInLibrary));
        Rescan();
        _host.Persist();
        _host.RaiseCounts();

        StatusText = IsBuiltInLibrary
            ? $"视频库回到内置目录：{LibraryDirText}"
            : $"视频库改到 {LibraryDirText}（含子文件夹）。";
        _host.Log(StatusText);
    }

    [RelayCommand]
    private void RemoveLocalVideo(VideoItem? item)
    {
        if (item is null)
        {
            return;
        }

        VideoLibraryStore.Delete(item);
        Rescan();
        StatusText = "已删除：" + item.Name;
    }

    [RelayCommand]
    private async Task PlayLocalVideo(VideoItem? item)
    {
        if (item is null)
        {
            return;
        }

        await PlayFileAsync(item.FilePath, item.Name);
    }

    [RelayCommand]
    private void AddSource()
    {
        VideoSource draft = new() { Name = NextSourceName() };
        if (!VideoSourceEditDialog.Show(null, draft, isNew: true))
        {
            return;
        }

        Subscribe(draft);
        Sources.Add(draft);
        SelectedSource = draft;
        SyncData();
        StatusText = "已添加在线视频源：" + draft.Name;
    }

    /// <summary>默认名不重名：新的在线视频源、新的在线视频源 2、3…</summary>
    private string NextSourceName()
    {
        const string baseName = "新的在线视频源";
        HashSet<string> used = new(Sources.Select(source => source.Name), StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(baseName))
        {
            return baseName;
        }

        for (int index = 2; ; index++)
        {
            string candidate = $"{baseName} {index}";
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    [RelayCommand]
    private void EditSource()
    {
        if (SelectedSource is null)
        {
            return;
        }

        VideoSource draft = new()
        {
            Id = SelectedSource.Id,
            Name = SelectedSource.Name,
            Url = SelectedSource.Url,
            Kind = SelectedSource.Kind,
            JsonFieldPath = SelectedSource.JsonFieldPath,
            Headers = SelectedSource.Headers,
            AutoSave = SelectedSource.AutoSave,
            Enabled = SelectedSource.Enabled,
        };

        if (!VideoSourceEditDialog.Show(null, draft, isNew: false))
        {
            return;
        }

        SelectedSource.Name = draft.Name;
        SelectedSource.Url = draft.Url;
        SelectedSource.Kind = draft.Kind;
        SelectedSource.JsonFieldPath = draft.JsonFieldPath;
        SelectedSource.Headers = draft.Headers;
        SelectedSource.AutoSave = draft.AutoSave;
        SelectedSource.Enabled = draft.Enabled;
        SyncData();
        StatusText = "已保存源：" + SelectedSource.Name;
    }

    [RelayCommand]
    private void RemoveSource()
    {
        if (SelectedSource is null)
        {
            return;
        }

        string name = SelectedSource.Name;
        Sources.Remove(SelectedSource);
        SelectedSource = Sources.FirstOrDefault();
        SyncData();
        StatusText = "已删除在线源：" + name;
    }

    /// <summary>把预览框里这段视频设为当前桌面视频壁纸。</summary>
    [RelayCommand]
    private async Task SetCurrentVideo()
    {
        string? path = PreviewSource?.LocalPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            StatusText = "预览框里还没有视频，先点“刷新”。";
            return;
        }

        await PlayFileAsync(path, Path.GetFileName(path));
    }

    [RelayCommand]
    private async Task PlaySource()
    {
        if (SelectedSource is null)
        {
            return;
        }

        VideoSource source = SelectedSource;
        if (string.IsNullOrWhiteSpace(source.Url))
        {
            StatusText = "这个源还没有填写地址。";
            return;
        }

        string? path = null;
        await _host.RunAsync($"正在获取「{source.Name}」视频…", async token =>
        {
            // 预览一律走缓存，不落保存位置；自动保存只作用于桌面换图那条路径。
            path = await OnlineVideoService.AcquireAsync(source, DataPaths.VideoCacheDir, keep: false, token);
        });
        if (path is null)
        {
            return;
        }

        PreviewSource = null;
        PreviewSource = new Uri(path);
        StatusText = $"正在预览：{source.Name}";
        _host.StatusText = $"在线视频预览：{source.Name}";
    }

    [RelayCommand]
    private void OpenVideoFolder() =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{LibraryDirText}\"")
        {
            UseShellExecute = true,
        });

    /// <summary>关掉启用开关 = 停止视频壁纸，并把这块屏恢复成最近一次的图片壁纸。</summary>
    private async Task StopVideoAndRestoreAsync()
    {
        if (!VideoWallpaperService.IsRunning)
        {
            return;
        }

        try
        {
            await Task.Run(VideoWallpaperService.Stop);

            string lastImage = _monitor.Data.LastApplied;
            if (!string.IsNullOrWhiteSpace(lastImage)
                && File.Exists(lastImage)
                && !VideoLibraryStore.Extensions.Contains(Path.GetExtension(lastImage).ToLowerInvariant()))
            {
                await _monitor.ApplyPathAsync(lastImage, "视频壁纸已停止，恢复图片壁纸", CancellationToken.None);
            }
            else
            {
                _host.StatusText = "视频壁纸已停止，桌面已回到图片壁纸。";
            }

            StatusText = "视频壁纸已停止。";
        }
        catch (Exception ex)
        {
            _host.Log("停止视频壁纸失败：" + ex.Message);
        }
    }

    /// <summary>概览页这块屏的「换一张」选中视频来源时调用：本地库优先，其次在线源。</summary>
    public async Task<bool> PlayRandomAsync(CancellationToken token)
    {
        if (LocalEnabled && LocalVideos.Count > 0)
        {
            VideoItem item = LocalVideos[Random.Shared.Next(LocalVideos.Count)];
            await PlayFileAsync(item.FilePath, item.Name);
            return true;
        }

        if (OnlineEnabled)
        {
            VideoSource? source = Sources
                .Where(source => source.Enabled)
                .OrderBy(_ => Random.Shared.Next())
                .FirstOrDefault();
            if (source is not null && !string.IsNullOrWhiteSpace(source.Url))
            {
                bool keep = source.AutoSave;
                string targetDir = keep ? SaveDirForSource(source) : DataPaths.VideoCacheDir;
                string path = await OnlineVideoService.AcquireAsync(source, targetDir, keep, token);
                await PlayFileAsync(path, source.Name);
                return true;
            }
        }

        return false;
    }

    private async Task PlayFileAsync(string path, string label)
    {
        try
        {
            await Task.Run(() => VideoWallpaperService.Start(path));
            _data.LastPlayed = path;
            CurrentVideoText = label;
            StatusText = $"正在播放：{label}";
            _host.StatusText = $"{_monitor.Label} 视频壁纸已播放：{label}";
            _host.Log($"视频壁纸（{_monitor.Label}）：{label}");
            _host.Persist();
        }
        catch (Exception ex)
        {
            StatusText = "播放失败：" + ex.Message;
            _host.StatusText = "视频壁纸播放失败：" + ex.Message;
            _host.Log("视频壁纸播放失败：" + ex.Message);
        }
    }

    private void Subscribe(VideoSource source) =>
        source.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(VideoSource.Enabled) or nameof(VideoSource.Name))
            {
                OnPropertyChanged(nameof(EnabledSourceCount));
                OnPropertyChanged(nameof(HasContent));
                OnPropertyChanged(nameof(OnlineSummary));
                _host.RaiseCounts();

                if (ReferenceEquals(source, SelectedSource))
                {
                    OnPropertyChanged(nameof(SelectedSaveDirText));
                }

                // 只有这两类改动影响落盘内容，其他属性（如 Url）由编辑对话框统一 SyncData。
                _host.Persist();
            }
        };

    /// <summary>按当前库目录重新扫描视频文件（含子文件夹），与图片库一样目录驱动；扫描在后台，列表在 UI 线程铺。</summary>
    private void Rescan()
    {
        _scanCts?.Cancel();
        CancellationTokenSource cts = new();
        _scanCts = cts;
        CancellationToken token = cts.Token;

        string dir = LibraryDirText;
        _ = RescanCoreAsync(dir, cts, token);
    }

    private async Task RescanCoreAsync(string dir, CancellationTokenSource cts, CancellationToken token)
    {
        List<VideoItem> items = new();
        try
        {
            await Task.Run(() =>
            {
                if (!Directory.Exists(dir))
                {
                    return;
                }

                List<string> files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                    .Where(file => VideoLibraryStore.Extensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .ToList();

                foreach (string file in files)
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    items.Add(new VideoItem
                    {
                        Name = Path.GetFileNameWithoutExtension(file),
                        FilePath = file,
                        SizeBytes = new FileInfo(file).Length,
                        IsFavorite = _favorites.Contains(file),
                    });
                }
            }, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _host.Log($"扫描视频库失败（{dir}）：{ex.Message}");
        }

        if (cts != _scanCts || token.IsCancellationRequested)
        {
            return;
        }

        LocalVideos.Clear();
        foreach (VideoItem item in items)
        {
            LocalVideos.Add(item);
        }

        if (SelectedLocalVideo is null || !LocalVideos.Contains(SelectedLocalVideo))
        {
            SelectedLocalVideo = LocalVideos.FirstOrDefault();
        }

        ApplyFilter();
        OnPropertyChanged(nameof(LocalCount));
        OnPropertyChanged(nameof(HasLocalVideos));
        OnPropertyChanged(nameof(HasContent));
        OnPropertyChanged(nameof(LocalSummary));

        _ = LoadThumbnailsAsync();
    }

    /// <summary>顺序补齐首帧缩略图；没缓存时拉起 --video-thumb 子进程抓帧。</summary>
    private async Task LoadThumbnailsAsync()
    {
        // 先 Cancel 并换新 CTS；旧实例等循环退出后再 Dispose，避免运行中的 token 被 Dispose。
        _thumbCts?.Cancel();
        CancellationTokenSource cts = new();
        CancellationTokenSource? old = _thumbCts;
        _thumbCts = cts;
        old?.Dispose();
        CancellationToken token = cts.Token;

        foreach (VideoItem item in LocalVideos)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            if (item.Thumbnail is not null)
            {
                continue;
            }

            try
            {
                ImageSource? image = await VideoThumbnailStore.EnsureAsync(item.FilePath, token);
                if (image is not null && !token.IsCancellationRequested)
                {
                    item.Thumbnail = image;
                }
            }
            catch (Exception)
            {
            }
        }

        if (_thumbCts == cts)
        {
            _thumbCts = null;
            cts.Dispose();
        }
    }

    private void ApplyFilter()
    {
        string key = (SearchText ?? string.Empty).Trim();
        View.Clear();
        foreach (VideoItem item in LocalVideos)
        {
            if (FavoritesOnly && !item.IsFavorite)
            {
                continue;
            }

            if (key.Length == 0 || item.Name.Contains(key, StringComparison.OrdinalIgnoreCase))
            {
                View.Add(item);
            }
        }

        OnPropertyChanged(nameof(FilteredCount));
    }

    /// <summary>收藏 / 取消收藏这个视频（目录驱动下按文件路径记）。</summary>
    [RelayCommand]
    private void ToggleFavoriteItem(VideoItem? item)
    {
        if (item is null)
        {
            return;
        }

        if (_favorites.Remove(item.FilePath))
        {
            item.IsFavorite = false;
        }
        else
        {
            _favorites.Add(item.FilePath);
            item.IsFavorite = true;
        }

        _data.FavoritePaths = _favorites.ToList();
        _host.Persist();
        ApplyFilter();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnFavoritesOnlyChanged(bool value) => ApplyFilter();

    partial void OnSelectedSourceChanged(VideoSource? value)
    {
        OnPropertyChanged(nameof(HasSelectedSource));
        OnPropertyChanged(nameof(SelectedSaveDirText));

        // 预览默认不显示：换源就清掉，等用户点“刷新”再取。
        PreviewSource = null;
    }

    partial void OnPreviewSourceChanged(Uri? value) => OnPropertyChanged(nameof(HasPreview));

    private void SyncData()
    {
        _data.Sources = Sources.ToList();
        _host.Persist();
        _host.RaiseCounts();

        OnPropertyChanged(nameof(LocalCount));
        OnPropertyChanged(nameof(SourceCount));
        OnPropertyChanged(nameof(EnabledSourceCount));
        OnPropertyChanged(nameof(HasLocalVideos));
        OnPropertyChanged(nameof(HasSources));
        OnPropertyChanged(nameof(HasContent));
        OnPropertyChanged(nameof(LocalSummary));
        OnPropertyChanged(nameof(OnlineSummary));
    }

    partial void OnLocalEnabledChanged(bool value)
    {
        _data.LocalEnabled = value;
        OnPropertyChanged(nameof(HasContent));
        OnPropertyChanged(nameof(LocalSummary));
        _host.Persist();
        _host.RaiseCounts();

        // 只有本地库和在线库都关掉，才算彻底不用视频壁纸。
        if (!LocalEnabled && !OnlineEnabled)
        {
            _ = StopVideoAndRestoreAsync();
        }
    }

    partial void OnOnlineEnabledChanged(bool value)
    {
        _data.OnlineEnabled = value;
        OnPropertyChanged(nameof(HasContent));
        OnPropertyChanged(nameof(OnlineSummary));
        _host.Persist();
        _host.RaiseCounts();

        if (!LocalEnabled && !OnlineEnabled)
        {
            _ = StopVideoAndRestoreAsync();
        }
    }
}
