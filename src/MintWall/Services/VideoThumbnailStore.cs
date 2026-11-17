using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
        Dictionary<string, ImageSource?> batch = await EnsureManyAsync(new[] { videoPath }, token);
        return batch.TryGetValue(videoPath, out ImageSource? image) ? image : null;
    }

    /// <summary>
    /// 批量补齐缩略图：缓存命中的直接返回，缺的一次性交给一个 --video-thumbs 子进程抓。
    /// 一个进程抓 N 条，省掉 N-1 次 WPF 启动（实测 10 条从 ~12.8s 降到 ~6s）。
    /// </summary>
    public static async Task<Dictionary<string, ImageSource?>> EnsureManyAsync(
        IReadOnlyList<string> videos,
        CancellationToken token)
    {
        Dictionary<string, ImageSource?> result = new(StringComparer.OrdinalIgnoreCase);
        List<(string Video, string Thumb, string Temp)> jobs = new();

        foreach (string video in videos)
        {
            if (string.IsNullOrWhiteSpace(video) || result.ContainsKey(video))
            {
                continue;
            }

            string thumb = ThumbPathFor(video);
            ImageSource? cached = Load(thumb);
            if (cached is not null)
            {
                result[video] = cached;
                continue;
            }

            result[video] = null;
            jobs.Add((video, thumb, thumb + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp"));
        }

        if (jobs.Count == 0)
        {
            return result;
        }

        string? exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            return result;
        }

        string request = Path.Combine(DataPaths.ThumbDir, "thumbs-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            Directory.CreateDirectory(DataPaths.ThumbDir);
            JsonSerializerOptions options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            File.WriteAllText(
                request,
                JsonSerializer.Serialize(jobs.Select(job => new { job.Video, Out = job.Temp }).ToArray(), options));

            using Process process = Process.Start(new ProcessStartInfo(exe, $"--video-thumbs \"{request}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            })!;

            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromSeconds(12 + jobs.Count * 8));
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill();
                }
                catch (InvalidOperationException)
                {
                }
            }

            foreach ((string video, string thumb, string temp) in jobs)
            {
                try
                {
                    if (File.Exists(temp))
                    {
                        File.Move(temp, thumb, overwrite: true);
                    }
                }
                catch (IOException)
                {
                }

                result[video] = Load(thumb);
            }
        }
        catch (Exception)
        {
            // 子进程起不来/清单写不了：保持 null，界面显示占位图标，下次刷新再试。
        }
        finally
        {
            try
            {
                File.Delete(request);
            }
            catch (IOException)
            {
            }

            foreach ((_, _, string temp) in jobs)
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

        return result;
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
