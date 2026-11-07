using System.IO;
using Microsoft.Win32;
using MintWall.Models;

namespace MintWall.Services;

/// <summary>
/// 找系统本机的 Windows 聚焦图：先看聚焦自己的资源目录，再看 TranscodedWallpaper，最后退回注册表里正在用的那张。
/// 没装聚焦组件的机器（LTSC、精简版）会落到最后一档，拿到的就是当前桌面图。
/// </summary>
public static class SpotlightService
{
    /// <summary>内置聚焦源的地址占位符，不参与网络请求。</summary>
    public const string SourceUrl = "spotlight://local";

    public static SourceProfile BuiltInSource() => new()
    {
        Id = "spotlight",
        Name = "Windows 聚焦",
        Url = SourceUrl,
        Kind = SourceKind.Spotlight,
    };

    public static string? LocateCurrent()
    {
        string? fromAssets = NewestInAssets();
        if (fromAssets is not null)
        {
            return fromAssets;
        }

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string transcoded = Path.Combine(local, @"Microsoft\Windows\Themes\TranscodedWallpaper");
        if (IsImage(transcoded))
        {
            return transcoded;
        }

        return FromRegistry();
    }

    private static string? NewestInAssets()
    {
        string packages = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");

        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(packages, "Microsoft.Windows.ContentDeliveryManager_*")
                .Select(dir => Path.Combine(dir, @"LocalState\Assets"));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        List<(string Path, DateTime Written)> candidates = new();
        foreach (string assets in dirs)
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(assets))
                {
                    if (IsImage(file))
                    {
                        candidates.Add((file, File.GetLastWriteTimeUtc(file)));
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

        return candidates.Count == 0 ? null : candidates.MaxBy(item => item.Written).Path;
    }

    private static string? FromRegistry()
    {
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using RegistryKey? key = baseKey.OpenSubKey(@"Control Panel\Desktop");
            string? value = key?.GetValue("Wallpaper") as string;
            return !string.IsNullOrWhiteSpace(value) && IsImage(value) ? value : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsImage(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length < 1024)
            {
                return false;
            }

            using FileStream stream = File.OpenRead(path);
            byte[] head = new byte[4];
            int read = stream.Read(head, 0, head.Length);
            if (read < 3)
            {
                return false;
            }

            return (head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
                || (head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47)
                || (head[0] == 0x42 && head[1] == 0x4D)
                || (head[0] == 0x47 && head[1] == 0x49 && head[2] == 0x46);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
