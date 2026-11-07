using System.IO;
using SkiaSharp;
using MintWall.Models;

namespace MintWall.Services;

public sealed class LibraryService
{
    private static readonly string[] AcceptedExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp" };

    private readonly LibraryMetaStore _meta = new();

    public LibraryService(string directory)
    {
        Directory = directory;
        // 用户自己挑的目录常按相册分子文件夹，内置目录只认顶层。
        Recursive = !DataPaths.IsInsideRoot(directory);
    }

    /// <summary>这个实例负责的那个壁纸库目录（桌面或锁屏）。</summary>
    public string Directory { get; }

    /// <summary>用户自选目录要连子文件夹一起收进来。</summary>
    public bool Recursive { get; }

    public IReadOnlyList<string> ListFiles() => EnumerateAccepted(Directory, Recursive);

    /// <summary>把根目录里散放的图片搬进某块屏的子目录；同名不覆盖，搬不动的留给下次。</summary>
    public static int MoveLooseFiles(string fromDir, string toDir)
    {
        if (string.Equals(Path.GetFullPath(fromDir), Path.GetFullPath(toDir), StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        int moved = 0;
        foreach (string path in EnumerateAccepted(fromDir, false))
        {
            string target = Path.Combine(toDir, Path.GetFileName(path));
            if (File.Exists(target))
            {
                continue;
            }

            try
            {
                System.IO.Directory.CreateDirectory(toDir);
                File.Move(path, target);
                moved++;
            }
            catch (IOException)
            {
                // 资源管理器正占用的文件保持原位，下次启动再搬。
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return moved;
    }

    /// <summary>缓存目录只留正在用的那一张；收藏过的不删，免得用户刚点完收藏就丢图。</summary>
    public int PruneCache(string cacheDir, string keepPath, IReadOnlyCollection<string> favoriteIds)
    {
        int removed = 0;
        foreach (string path in EnumerateAccepted(cacheDir, false))
        {
            if (string.Equals(path, keepPath, StringComparison.OrdinalIgnoreCase)
                || favoriteIds.Contains(Path.GetFileNameWithoutExtension(path)))
            {
                continue;
            }

            try
            {
                File.Delete(path);
                _meta.Remove(path);
                removed++;
            }
            catch (IOException)
            {
                // 还被系统占着的那张留给下次清理。
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }

    private const int ScanCap = 4000;

    private static IReadOnlyList<string> EnumerateAccepted(string directory, bool recursive)
    {
        List<string> found = new();

        try
        {
            IEnumerable<string> candidates = recursive
                ? System.IO.Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                : System.IO.Directory.EnumerateFiles(directory);

            foreach (string path in candidates)
            {
                if (!AcceptedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
                {
                    continue;
                }

                found.Add(path);
                if (found.Count >= ScanCap)
                {
                    break;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return found.OrderByDescending(SafeWriteTime).ToList();
    }

    private static DateTime SafeWriteTime(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (IOException)
        {
            return DateTime.MinValue;
        }
    }

    public LibraryItem ToItem(string path, IReadOnlyCollection<string> favoriteIds)
    {
        LibraryMeta meta = _meta.Describe(path);
        return new LibraryItem
        {
            Id = Path.GetFileNameWithoutExtension(path),
            FilePath = path,
            SourceUrl = meta.SourceUrl,
            Bytes = meta.Bytes > 0 ? meta.Bytes : SafeLength(path),
            Width = meta.Width,
            Height = meta.Height,
            CreatedAt = meta.SavedAt,
            IsFavorite = favoriteIds.Contains(Path.GetFileNameWithoutExtension(path)),
        };
    }

    /// <summary>Copies an arbitrary user file into the library, normalizing it to a Windows-friendly format.</summary>
    public AcquiredImage ImportLocal(string sourcePath, EncodePreference encode)
    {
        byte[] body = File.ReadAllBytes(sourcePath);
        List<string> trace = new() { $"本地导入 {Path.GetFileName(sourcePath)} {ImageService.FormatSize(body.LongLength)}" };
        AcquiredImage acquired = ImageService.SaveToLibrary(body, $"local:{sourcePath}", Directory, encode, trace);
        _meta.Put(acquired.FilePath, acquired.Width, acquired.Height, acquired.SourceUrl);
        return acquired;
    }

    public void Record(AcquiredImage acquired) => _meta.Put(acquired.FilePath, acquired.Width, acquired.Height, acquired.SourceUrl);

    public void Delete(string path)
    {
        File.Delete(path);
        _meta.Remove(path);
        string thumb = ThumbnailStore.ThumbPathFor(path);
        if (File.Exists(thumb))
        {
            File.Delete(thumb);
        }
    }

    public void OpenFolder()
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{Directory}\"")
        {
            UseShellExecute = true,
        });
    }

    /// <summary>Keeps the newest N files, skipping favorites, so the library cannot grow without bound.</summary>
    public int Trim(IReadOnlyCollection<string> favoriteIds, int limit)
    {
        // 用户自己的文件夹不做自动清理，删图只能由用户在界面上点。
        if (limit <= 0 || Recursive)
        {
            return 0;
        }

        int removed = 0;
        foreach (string path in ListFiles().Skip(limit))
        {
            if (favoriteIds.Contains(Path.GetFileNameWithoutExtension(path)))
            {
                continue;
            }

            try
            {
                Delete(path);
                removed++;
            }
            catch (IOException)
            {
                // A file held by the shell stays for the next pass.
            }
        }

        return removed;
    }

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }
}
