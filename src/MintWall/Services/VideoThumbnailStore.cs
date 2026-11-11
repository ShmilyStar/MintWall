using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MintWall.Services;

/// <summary>视频首帧缩略图的缓存：按文件路径 + 修改时间命名，转交给独立进程生成。</summary>
public static class VideoThumbnailStore
{
    public static string ThumbPathFor(string videoPath)
    {
        long stamp = 0;
        try
        {
            stamp = new FileInfo(videoPath).LastWriteTimeUtc.Ticks;
        }
        catch (IOException)
        {
        }

        string key = videoPath.ToLowerInvariant() + "|video|" + stamp;
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(key));
        return Path.Combine(DataPaths.ThumbDir, "video-" + Convert.ToHexString(hash) + ".jpg");
    }

    /// <summary>缓存有就直接用，没有就拉起 --video-thumb 子进程抓一帧。</summary>
    public static async Task<ImageSource?> EnsureAsync(string videoPath, CancellationToken token)
    {
        string thumb = ThumbPathFor(videoPath);
        ImageSource? cached = Load(thumb);
        if (cached is not null)
        {
            return cached;
        }

        string? exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            return null;
        }

        string temp = thumb + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            Directory.CreateDirectory(DataPaths.ThumbDir);
            using Process process = Process.Start(new ProcessStartInfo(
                exe,
                $"--video-thumb \"{videoPath}\" \"{temp}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;

            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(cts.Token);

            if (!File.Exists(temp))
            {
                return null;
            }

            File.Move(temp, thumb, overwrite: true);
            return Load(thumb);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    private static ImageSource? Load(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            BitmapImage image = new();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
