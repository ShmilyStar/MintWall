using System.IO;
using MintWall.Models;
using MintWall.Native;

namespace MintWall.Services;

/// <summary>
/// 后台进程用的换图动作：与界面定时 tick 同一套语义（勾选来源里随机一路、逐屏下发、
/// 桌面顺带同步锁屏、回写 LastApplied、清缓存与库容量），不依赖 WPF。
/// </summary>
public sealed class AutoChangeEngine : IDisposable
{
    private readonly AppSettings _settings;
    private readonly ConfigStore _config = new();
    private readonly WallpaperService _wallpaper = new();
    private readonly Dictionary<(bool IsLock, string MonitorId), LibraryService> _libraries = new();

    public AutoChangeEngine(AppSettings settings)
    {
        _settings = settings;
    }

    /// <summary>打包版登录时按记录补一次桌面壁纸，避免注册表虚拟化导致登录回退（与界面启动同一逻辑）。</summary>
    public void RestoreLastDesktop()
    {
        if (!DataPaths.IsPackaged)
        {
            return;
        }

        foreach (MonitorSettings profile in _settings.Desktop.Monitors)
        {
            if (!File.Exists(profile.LastApplied) || OnlineApplyTarget(profile) is not { } target)
            {
                continue;
            }

            _wallpaper.Apply(profile.LastApplied, target, profile.Style);
            WorkerLog.Write($"已恢复桌面壁纸：{profile.LastApplied}");
            if (!_wallpaper.SupportsPerMonitor)
            {
                break;
            }
        }
    }

    public bool IsMonitorOnline(MonitorSettings profile) => OnlineApplyTarget(profile) is not null;

    public async Task ChangeAsync(bool isLock, MonitorSettings profile, CancellationToken ct)
    {
        LibraryService library = LibraryFor(isLock, profile);
        string current = isLock
            ? LockScreenService.ReadCurrentPath() ?? string.Empty
            : _wallpaper.CurrentWallpaperOf(profile.MonitorId);

        FetchChannel channel = ResolveChannel(profile, library);
        if (channel == FetchChannel.Library)
        {
            if (NextLibraryPath(library, isLock, current) is { } item)
            {
                await ApplyAsync(isLock, profile, item, ct);
                Finish(library);
                return;
            }

            // 库里没有能用的图（例如锁屏库全是 webp），退到内置图。
            channel = FetchChannel.BuiltIn;
        }

        SourceProfile? picked = channel == FetchChannel.Sources ? ResolveRandomSource(profile) : null;
        SourceProfile source = picked ?? BingService.BuiltInSource();

        string targetDir = source.AutoSave
            ? DataPaths.ResolveSaveDir(isLock, profile)
            : DataPaths.CacheDirFor(isLock);
        AcquiredImage acquired = await ImageService.AcquireAsync(source, targetDir, _settings.Encode, ct);
        library.Record(acquired);
        await ApplyAsync(isLock, profile, acquired.FilePath, ct);
        if (!source.AutoSave)
        {
            library.PruneCache(targetDir, acquired.FilePath, _settings.Favorites);
        }

        Finish(library);
    }

    private async Task ApplyAsync(bool isLock, MonitorSettings profile, string path, CancellationToken ct)
    {
        if (isLock)
        {
            LockScreenOutcome outcome = await LockScreenService.ApplyAsync(path, ct);
            profile.LastApplied = outcome.AppliedPath ?? path;
            WorkerLog.Write(outcome.Success
                ? $"锁屏·{profile.Label} ← {Path.GetFileName(path)}"
                : $"锁屏·{profile.Label} 设置失败：{outcome.Message}");
            return;
        }

        string target = OnlineApplyTarget(profile) ?? Wallpapers.AllMonitors;
        _wallpaper.Apply(path, target, profile.Style);
        profile.LastApplied = path;
        WorkerLog.Write($"桌面·{profile.Label} ← {Path.GetFileName(path)}");

        if (!_settings.Desktop.SyncLockScreen)
        {
            return;
        }

        MonitorSettings? lockProfile =
            _settings.Lock.Monitors.FirstOrDefault(m => m.MonitorId == profile.MonitorId)
            ?? _settings.Lock.Monitors.FirstOrDefault();
        if (lockProfile is null)
        {
            return;
        }

        LockScreenOutcome lockOutcome = await LockScreenService.ApplyAsync(path, ct);
        lockProfile.LastApplied = lockOutcome.AppliedPath ?? path;
        if (!lockOutcome.Success)
        {
            WorkerLog.Write("锁屏同步失败：" + lockOutcome.Message);
        }
    }

    private void Finish(LibraryService library)
    {
        library.Trim(_settings.Favorites, Math.Clamp(_settings.LibraryLimit, 10, 100000));
        library.FlushMeta();
        _config.Save(_settings);
    }

    private string? OnlineApplyTarget(MonitorSettings profile)
    {
        foreach (MonitorInfo screen in _wallpaper.GetMonitors())
        {
            if (string.Equals(screen.Id, profile.MonitorId, StringComparison.Ordinal))
            {
                return screen.ApplyTarget;
            }
        }

        return null;
    }

    private LibraryService LibraryFor(bool isLock, MonitorSettings profile)
    {
        (bool, string) key = (isLock, profile.MonitorId);
        if (!_libraries.TryGetValue(key, out LibraryService? library))
        {
            library = new LibraryService(DataPaths.ResolveLibraryDir(isLock, profile));
            _libraries[key] = library;
        }

        return library;
    }

    private FetchChannel ResolveChannel(MonitorSettings profile, LibraryService library)
    {
        bool hasEnabledSources = profile.Sources.Any(source => source.Enabled);
        List<FetchChannel> enabled = new(3);
        if (profile.FetchFromBuiltIn)
        {
            enabled.Add(FetchChannel.BuiltIn);
        }

        if (profile.FetchFromLibrary && library.ListFiles().Count > 0)
        {
            enabled.Add(FetchChannel.Library);
        }

        if (profile.FetchFromSources && hasEnabledSources)
        {
            enabled.Add(FetchChannel.Sources);
        }

        if (enabled.Count == 0)
        {
            return hasEnabledSources ? FetchChannel.Sources : FetchChannel.BuiltIn;
        }

        return enabled[Random.Shared.Next(enabled.Count)];
    }

    private static string? NextLibraryPath(LibraryService library, bool isLock, string current)
    {
        List<string> pool = library.ListFiles()
            .Where(path => !isLock || Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png")
            .ToList();
        if (pool.Count > 1)
        {
            pool.RemoveAll(path => string.Equals(path, current, StringComparison.OrdinalIgnoreCase));
        }

        return pool.Count == 0 ? null : pool[Random.Shared.Next(pool.Count)];
    }

    private static SourceProfile? ResolveRandomSource(MonitorSettings profile)
    {
        List<SourceProfile> usable = profile.Sources.Where(source => source.Enabled).ToList();
        return usable.Count == 0 ? null : usable[Random.Shared.Next(usable.Count)];
    }

    public void Dispose() => DesktopWallpaperFactory.Release();

    private enum FetchChannel
    {
        BuiltIn,
        Library,
        Sources,
    }
}