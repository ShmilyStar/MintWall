using System.IO;
using MintWall.Models;

namespace MintWall.Services;

/// <summary>本地视频库的落盘位置：导入时把视频复制进来，删条目时删文件。</summary>
public static class VideoLibraryStore
{
    public static readonly string[] Extensions = { ".mp4", ".wmv", ".avi", ".mov", ".m4v" };

    /// <summary>把选中的本地视频复制进这块屏的库目录，返回新条目（原文件和库文件互不影响）。</summary>
    public static VideoItem ImportLocal(string sourcePath, string targetDir)
    {
        string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        string name = Path.GetFileNameWithoutExtension(sourcePath);
        string target = Path.Combine(
            targetDir,
            DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8] + ext);

        Directory.CreateDirectory(targetDir);
        File.Copy(sourcePath, target, overwrite: false);

        return new VideoItem
        {
            Name = name,
            FilePath = target,
            SizeBytes = new FileInfo(target).Length,
        };
    }

    public static void Delete(VideoItem item)
    {
        try
        {
            if (File.Exists(item.FilePath))
            {
                File.Delete(item.FilePath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>在线视频下载到缓存目录用的新文件名。</summary>
    public static string CacheFile(string extension) =>
        Path.Combine(
            DataPaths.VideoCacheDir,
            DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8] + extension);
}
