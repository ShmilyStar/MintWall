using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using MintWall.Models;
using MintWall.Native;

namespace MintWall.Services;

/// <summary>
/// Headless entry point, so the same engine can be scripted and diagnosed without the window.
/// </summary>
public static class CliRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        AttachToConsole();
        string verb = args[0].ToLowerInvariant();

        try
        {
            return verb switch
            {
                "probe" => await ProbeAsync(args),
                "set" => await SetAsync(args),
                "auto" => await AutoAsync(args),
                "list" => List(args),
                "uninstall" or "--uninstall" or "purge" => Uninstall(),
                "help" or "-h" or "--help" => Help(),
                _ => Unknown(verb),
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine("错误：" + ex.Message);
            return 1;
        }
    }

    private static int Unknown(string verb)
    {
        Console.WriteLine($"未知命令：{verb}");
        Help();
        return 64;
    }

    private static int Help()
    {
        Console.WriteLine("""
            MintWall 命令行
              probe <url> [--kind auto|image|json] [--field data.url] [--headers "Key: Value"] [--apply] [--target desktop|lock] [--screen 1] [--monitor all|<id>] [--style fill]
                  诊断接口：打印跳转、Content-Type、尺寸、入库格式。加 --apply 会真的换壁纸。
              set <图片路径> [--target desktop|lock] [--screen 1] [--monitor all|<id>] [--style fill|fit|stretch|center|tile|span]
                  把本地图片导入该屏的壁纸库并设为壁纸。
              auto [--target desktop|lock] [--screen 1] [--source <名称或ID>]
                  从该屏启用的源里随机取一张并应用，等同定时任务执行的一次动作。
              list [--target desktop|lock]
                  列出每块屏的壁纸源、各自的壁纸库、保存位置与显示器状态。
              uninstall
                  删除默认数据目录（设置、壁纸库、缓存、缩略图、保存归档）和开机自启；
                  用户自选的壁纸库/保存目录保持不动。
              说明：--target 缺省 desktop；--screen 选第几块屏（序号、名称或 id 片段），缺省第 1 块。
                    桌面、锁屏和每块屏各自维护壁纸源与壁纸库，互不影响。
            """);
        return 0;
    }

    /// <summary>--screen 选中的那份逐屏配置；没配置过任何屏时返回 null。</summary>
    private static MonitorSettings? PickProfile(AppSettings settings, bool lockTarget, string[] args)
    {
        TargetSettings data = settings.For(lockTarget);
        if (data.Monitors.Count == 0)
        {
            return null;
        }

        string? want = Option(args, "--screen");
        if (want is null)
        {
            return data.Monitors[0];
        }

        if (int.TryParse(want, out int index) && index >= 1 && index <= data.Monitors.Count)
        {
            return data.Monitors[index - 1];
        }

        return data.Monitors.FirstOrDefault(m =>
            m.MonitorId.Equals(want, StringComparison.OrdinalIgnoreCase)
            || m.Label.Contains(want, StringComparison.OrdinalIgnoreCase)
            || m.MonitorId.Contains(want, StringComparison.OrdinalIgnoreCase)) ?? data.Monitors[0];
    }

    private static string ProfileName(MonitorSettings profile, bool lockTarget) =>
        $"{(lockTarget ? "锁屏" : "桌面")}·{profile.Label}";

    private static string LibraryDirOf(bool lockTarget, MonitorSettings? profile) =>
        DataPaths.ResolveLibraryDir(lockTarget, profile ?? new MonitorSettings());

    private static async Task<int> ProbeAsync(string[] args)
    {
        string? url = args.Skip(1).FirstOrDefault(a => !a.StartsWith("-"));
        if (url is null)
        {
            Console.WriteLine("缺少接口地址。");
            return 64;
        }

        SourceProfile source = new()
        {
            Name = "命令行",
            Url = url,
            Kind = ParseKind(Option(args, "--kind") ?? "auto"),
            JsonFieldPath = Option(args, "--field") ?? string.Empty,
            Headers = Option(args, "--headers") ?? string.Empty,
        };

        EncodePreference encode = ParseEncode(Option(args, "--encode") ?? "auto");
        ProbeService probe = new();
        ProbeResult result = await probe.ProbeAsync(source, encode, CancellationToken.None);
        Console.WriteLine(result.Report);

        if (args.Contains("--apply"))
        {
            bool lockTarget = IsLockTarget(args);
            AppSettings settings = new ConfigStore().Load();
            MonitorSettings? profile = PickProfile(settings, lockTarget, args);
            LibraryService library = new(LibraryDirOf(lockTarget, profile));
            AcquiredImage acquired = await ImageService.AcquireAsync(source, library.Directory, encode, CancellationToken.None);
            library.Record(acquired);
            await ApplyAsync(acquired.FilePath, args, lockTarget, profile);
        }

        return 0;
    }

    private static async Task<int> SetAsync(string[] args)
    {
        string? path = args.Skip(1).FirstOrDefault(a => !a.StartsWith("-"));
        if (path is null || !File.Exists(path))
        {
            Console.WriteLine("图片不存在：" + path);
            return 64;
        }

        bool lockTarget = IsLockTarget(args);
        AppSettings settings = new ConfigStore().Load();
        MonitorSettings? profile = PickProfile(settings, lockTarget, args);
        EncodePreference encode = ParseEncode(Option(args, "--encode") ?? "auto");
        LibraryService library = new(LibraryDirOf(lockTarget, profile));
        AcquiredImage acquired = library.ImportLocal(path, encode);
        await ApplyAsync(acquired.FilePath, args, lockTarget, profile);
        Console.WriteLine($"已应用：{acquired.FilePath}（{acquired.Width}×{acquired.Height}）");
        return 0;
    }

    private static async Task<int> AutoAsync(string[] args)
    {
        AppSettings settings = new ConfigStore().Load();
        bool lockTarget = IsLockTarget(args);
        MonitorSettings? profile = PickProfile(settings, lockTarget, args);

        if (profile is null)
        {
            Console.WriteLine($"{(lockTarget ? "锁屏" : "桌面")}还没有识别到显示器，无法确定给哪块屏换壁纸。");
            return 1;
        }

        LibraryService library = new(LibraryDirOf(lockTarget, profile));

        string? selector = Option(args, "--source");
        List<SourceProfile> enabled = profile.Sources.Where(s => s.Enabled).ToList();
        SourceProfile? source = selector is null
            ? (enabled.Count > 0 ? enabled[Random.Shared.Next(enabled.Count)] : BingService.BuiltInSource())
            : profile.Sources.FirstOrDefault(s => s.Id == selector || s.Name.Equals(selector, StringComparison.OrdinalIgnoreCase));

        if (source is null)
        {
            Console.WriteLine($"{ProfileName(profile, lockTarget)} 没有名为「{selector}」的壁纸源，可先执行：MintWall list");
            return 1;
        }

        string targetDir = source.AutoSave ? DataPaths.ResolveSaveDir(lockTarget, profile) : DataPaths.CacheDirFor(lockTarget);
        AcquiredImage acquired = await ImageService.AcquireAsync(source, targetDir, settings.Encode, CancellationToken.None);
        library.Record(acquired);
        await ApplyAsync(acquired.FilePath, args, lockTarget, profile);
        if (!source.AutoSave)
        {
            library.PruneCache(targetDir, acquired.FilePath, settings.Favorites);
        }

        Console.WriteLine($"{ProfileName(profile, lockTarget)} 来源「{source.Name}」已应用：{acquired.Width}×{acquired.Height} {acquired.FilePath}");
        return 0;
    }

    private static async Task ApplyAsync(string imagePath, string[] args, bool lockTarget, MonitorSettings? profile)
    {
        if (lockTarget)
        {
            LockScreenOutcome outcome = await LockScreenService.ApplyAsync(imagePath, CancellationToken.None);
            Console.WriteLine(outcome.Success ? $"锁屏已更新：{imagePath}" : "锁屏设置失败：" + outcome.Message);
            return;
        }

        WallpaperService wallpaper = new();
        string monitor = Option(args, "--monitor") ?? profile?.MonitorId ?? Wallpapers.AllMonitors;
        if (monitor.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            monitor = Wallpapers.AllMonitors;
        }

        wallpaper.Apply(imagePath, monitor, ParseStyle(Option(args, "--style") ?? profile?.Style.ToString() ?? "fill"));
        Console.WriteLine($"桌面已应用：{imagePath} → {monitor}");
        Console.WriteLine("接口：" + wallpaper.EngineText);
    }

    private static bool IsLockTarget(string[] args) =>
        string.Equals(Option(args, "--target"), "lock", StringComparison.OrdinalIgnoreCase);

    private static int Uninstall()
    {
        Console.WriteLine("正在清理 MintWall 数据…");
        return UninstallService.Purge() ? 0 : 1;
    }

    private static int List(string[] args)
    {
        AppSettings settings = new ConfigStore().Load();
        bool lockTarget = IsLockTarget(args);
        TargetSettings data = settings.For(lockTarget);
        string label = lockTarget ? "锁屏" : "桌面";

        if (!lockTarget)
        {
            WallpaperService wallpaper = new();
            Console.WriteLine("== 显示器 ==");
            Console.WriteLine("  " + wallpaper.EngineText);
            foreach (MonitorInfo monitor in wallpaper.GetMonitors())
            {
                Console.WriteLine($"  {monitor.Label}  {monitor.SizeText}  id={Shorten(monitor.Id)}");
                Console.WriteLine($"      当前：{monitor.CurrentWallpaper}");
            }

            Console.WriteLine();
        }

        if (data.Monitors.Count == 0)
        {
            Console.WriteLine($"== {label} ==\n  还没有任何屏幕配置。");
            return 0;
        }

        for (int i = 0; i < data.Monitors.Count; i++)
        {
            MonitorSettings profile = data.Monitors[i];
            Console.WriteLine($"== {label} · {profile.Label}（--screen {i + 1}）==");

            foreach (SourceProfile source in profile.Sources)
            {
                string keep = source.AutoSave ? "  [自动保存]" : string.Empty;
                string disabled = source.Enabled ? string.Empty : "  [已停用]";
                Console.WriteLine($"  {source.Name}  [{source.Kind}]  {source.Url}{keep}{disabled}");
            }

            if (profile.Sources.Count == 0)
            {
                Console.WriteLine("  （还没有壁纸源，auto 会先用必应每日壁纸）");
            }

            LibraryService library = new(LibraryDirOf(lockTarget, profile));
            IReadOnlyList<string> files = library.ListFiles();
            string saveDir = DataPaths.ResolveSaveDir(lockTarget, profile);
            Console.WriteLine($"  壁纸库：{library.Directory}（{files.Count} 张）{(string.IsNullOrEmpty(profile.LibraryDirectory) ? "  [内置]" : "  [自选]")}");
            Console.WriteLine($"  保存位置：{saveDir}{(string.IsNullOrEmpty(profile.SaveDirectory) ? "  [内置]" : "  [自选]")}");
            foreach (string file in files.Take(5))
            {
                LibraryMeta meta = new LibraryMetaStore().Describe(file);
                Console.WriteLine($"      {Path.GetFileName(file)}  {meta.Width}×{meta.Height}  {ImageService.FormatSize(meta.Bytes)}  来源 {Shorten(meta.SourceUrl)}");
            }

            Console.WriteLine($"  定时：{(profile.Schedule.Enabled ? $"每 {profile.Schedule.IntervalMinutes} 分钟" : "关闭")}    样式：{profile.Style}    上次：{Shorten(profile.LastApplied)}");
            Console.WriteLine();
        }

        return 0;
    }

    private static string Shorten(string value) => value.Length <= 60 ? value : "..." + value[^57..];

    private static string? Option(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static SourceKind ParseKind(string value) => value.ToLowerInvariant() switch
    {
        "image" or "direct" or "img" => SourceKind.DirectImage,
        "json" => SourceKind.Json,
        _ => SourceKind.Auto,
    };

    private static EncodePreference ParseEncode(string value) => value.ToLowerInvariant() switch
    {
        "jpg" or "jpeg" => EncodePreference.Jpeg,
        "png" => EncodePreference.Png,
        _ => EncodePreference.Auto,
    };

    private static WallpaperStyleMode ParseStyle(string value) => value.ToLowerInvariant() switch
    {
        "fit" => WallpaperStyleMode.Fit,
        "stretch" => WallpaperStyleMode.Stretch,
        "center" => WallpaperStyleMode.Center,
        "tile" => WallpaperStyleMode.Tile,
        "span" => WallpaperStyleMode.Span,
        _ => WallpaperStyleMode.Fill,
    };

    private static void AttachToConsole()
    {
        try
        {
            if (!NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS))
            {
                NativeMethods.AllocConsole();
            }

            TextWriter output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            TextWriter error = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true };
            Console.SetOut(output);
            Console.SetError(error);
        }
        catch
        {
            // Started without a usable console; the exit code still reports the outcome.
        }
    }
}
