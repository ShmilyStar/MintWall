using System.IO;
using System.Security.Cryptography;
using System.Text;
using MintWall.Models;

namespace MintWall.Services;

public static class DataPaths
{
    static DataPaths()
    {
        IsPackaged = DetectPackaged();
        string baseDir = IsPackaged
            ? Windows.Storage.ApplicationData.Current.LocalFolder.Path
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string target = Path.Combine(baseDir, "MintWall");
        if (IsPackaged)
        {
            MigrateRealLocalToContainer(target);
        }
        MigrateLegacyRoot(baseDir);
        Root = target;
        DesktopWallpaperDir = Path.Combine(Root, "wallpapers");
        LockWallpaperDir = Path.Combine(Root, "wallpapers-lock");
        CacheDir = Path.Combine(Root, "cache");
        SaveDir = Path.Combine(Root, "saved");
        ThumbDir = Path.Combine(Root, "thumbs");
        SettingsFile = Path.Combine(Root, "settings.json");
        CreateDirectories();
    }

    private static bool DetectPackaged()
    {
        try
        {
            return Windows.ApplicationModel.Package.Current is not null;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    /// <summary>迁移到容器/新根目录没成功时置位；卸载清理据此保留真实目录，避免把没搬走的数据删掉。</summary>
    public static bool MigrationIncomplete { get; private set; }

    /// <summary>打包版早期把数据写在真实 LocalAppData，系统卸载带不走；首次进容器时整体搬进去。</summary>
    private static void MigrateRealLocalToContainer(string target)
    {
        string real = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MintWall");
        if (!Directory.Exists(real) || Directory.Exists(target))
        {
            return;
        }

        try
        {
            Directory.Move(real, target);
        }
        catch (IOException)
        {
            MigrationIncomplete = true;
        }
        catch (UnauthorizedAccessException)
        {
            MigrationIncomplete = true;
        }
    }

    /// <summary>包内运行时，配置里指向真实 LocalAppData 老根的路径一律改写到容器，保留相对子路径。</summary>
    public static string Retarget(string path)
    {
        if (!IsPackaged || string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        string prefix = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MintWall") + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Root, path[prefix.Length..])
            : path;
    }

    public static string Root { get; }

    /// <summary>桌面壁纸库根目录；按显示器拆分后，图片实际放在它下面的各屏子目录。</summary>
    public static string DesktopWallpaperDir { get; }

    /// <summary>锁屏壁纸库根目录，与桌面完全分开。</summary>
    public static string LockWallpaperDir { get; }

    public static string ThumbDir { get; }

    /// <summary>没开自动保存时，接口抓下来的图先落这里；换下一张就清掉。</summary>
    public static string CacheDir { get; }

    /// <summary>开了自动保存但用户没指定保存位置时，图归档到这里；这里的图不进壁纸库，也不参与清理。</summary>
    public static string SaveDir { get; }

    public static string SettingsFile { get; }

    /// <summary>True when running with MSIX package identity; data then lives in the package container so system uninstall removes it.</summary>
    public static bool IsPackaged { get; }

    public static string WallpaperDirFor(bool lockScreen) => lockScreen ? LockWallpaperDir : DesktopWallpaperDir;

    public static string CacheDirFor(bool lockScreen)
    {
        string dir = Path.Combine(CacheDir, lockScreen ? "lock" : "desktop");
        EnsureDirectory(dir);
        return dir;
    }

    /// <summary>这块屏真正用的壁纸库目录：用户选过就用用户的，没选就用内置的按屏子目录。</summary>
    public static string ResolveLibraryDir(bool lockScreen, MonitorSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.LibraryDirectory))
        {
            return LibraryDirFor(lockScreen, settings.MonitorId);
        }

        EnsureDirectory(settings.LibraryDirectory);
        return settings.LibraryDirectory;
    }

    /// <summary>这块屏的保存位置：用户选过就用用户的，没选就用内置的按屏归档目录。</summary>
    public static string ResolveSaveDir(bool lockScreen, MonitorSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.SaveDirectory))
        {
            return SaveDirFor(lockScreen, settings.MonitorId);
        }

        EnsureDirectory(settings.SaveDirectory);
        return settings.SaveDirectory;
    }

    public static string SaveDirFor(bool lockScreen, string monitorId)
    {
        string targetDir = lockScreen ? "lock" : "desktop";
        string key = KeyFor(lockScreen, monitorId);

        // 整屏统一目标直接用 saved\desktop / saved\lock，不再套一层同名子目录。
        string dir = string.Equals(key, targetDir, StringComparison.Ordinal)
            ? Path.Combine(SaveDir, targetDir)
            : Path.Combine(SaveDir, targetDir, key);

        EnsureDirectory(dir);
        return dir;
    }

    public static bool IsInsideRoot(string directory) =>
        directory.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || string.Equals(directory, Root, StringComparison.OrdinalIgnoreCase);

    /// <summary>某个目标在某块屏上的内置壁纸库目录，不存在时顺手创建。</summary>
    public static string LibraryDirFor(bool lockScreen, string monitorId)
    {
        string dir = Path.Combine(WallpaperDirFor(lockScreen), KeyFor(lockScreen, monitorId));
        EnsureDirectory(dir);
        return dir;
    }

    /// <summary>
    /// 旧版所有内置目录都叫 all，桌面和锁屏从路径上看不出区别。现在按目标分别用 desktop / lock，
    /// 启动时把旧目录里的图搬过去，避免升级后库突然变空。
    /// </summary>
    public static void MigrateLegacyAllFolders()
    {
        MoveFilesIfMissing(Path.Combine(DesktopWallpaperDir, "all"), Path.Combine(DesktopWallpaperDir, "desktop"));
        MoveFilesIfMissing(Path.Combine(LockWallpaperDir, "all"), Path.Combine(LockWallpaperDir, "lock"));

        // 1.4.0 之前整屏目标曾落到 saved\desktop\desktop，文件搬回上一层并删掉重复目录。
        MoveFilesIfMissing(Path.Combine(SaveDir, "desktop", "all"), Path.Combine(SaveDir, "desktop"));
        MoveFilesIfMissing(Path.Combine(SaveDir, "lock", "all"), Path.Combine(SaveDir, "lock"));
        MoveFilesIfMissing(Path.Combine(SaveDir, "desktop", "desktop"), Path.Combine(SaveDir, "desktop"));
        MoveFilesIfMissing(Path.Combine(SaveDir, "lock", "lock"), Path.Combine(SaveDir, "lock"));
    }

    private static void MoveFilesIfMissing(string from, string to)
    {
        if (!Directory.Exists(from))
        {
            return;
        }

        EnsureDirectory(to);
        foreach (string file in Directory.GetFiles(from))
        {
            string target = Path.Combine(to, Path.GetFileName(file));
            if (!File.Exists(target))
            {
                File.Move(file, target);
            }
        }

        try
        {
            Directory.Delete(from, recursive: false);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static void EnsureDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (IOException)
        {
            // 目录建不出来时后面的读写会自己报错，这里不额外打断界面。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>改名前数据放在 %LOCALAPPDATA%\WallpaperStudio，首次启动整体搬到新目录。</summary>
    private static void MigrateLegacyRoot(string baseDir)
    {
        string current = Path.Combine(baseDir, "MintWall");
        string legacy = Path.Combine(baseDir, "WallpaperStudio");
        if (Directory.Exists(current) || !Directory.Exists(legacy))
        {
            return;
        }

        try
        {
            Directory.Move(legacy, current);
        }
        catch (IOException)
        {
            MigrationIncomplete = true;
        }
        catch (UnauthorizedAccessException)
        {
            MigrationIncomplete = true;
        }
    }

    public static void EnsureCreated() => CreateDirectories();

    private static void CreateDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(DesktopWallpaperDir);
        Directory.CreateDirectory(LockWallpaperDir);
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(SaveDir);
        Directory.CreateDirectory(ThumbDir);
    }

    /// <summary>内置目录名：整屏统一目标用 desktop / lock，具体某块屏用它的稳定哈希名。</summary>
    private static string KeyFor(bool lockScreen, string monitorId) =>
        string.IsNullOrWhiteSpace(monitorId) || monitorId is Wallpapers.AllMonitors or Wallpapers.Primary
            ? (lockScreen ? "lock" : "desktop")
            : DirKey(monitorId);

    /// <summary>设备路径里有 \ / # 等非法字符，用「可读前缀 + 稳定哈希」当目录名。</summary>
    private static string DirKey(string monitorId)
    {
        string letters = new(monitorId.Where(char.IsLetterOrDigit).ToArray());
        string prefix = letters.Length == 0
            ? "screen"
            : letters[..Math.Min(letters.Length, 12)].ToLowerInvariant();
        string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(monitorId)))
            [..8]
            .ToLowerInvariant();

        return prefix + "-" + hash;
    }
}
