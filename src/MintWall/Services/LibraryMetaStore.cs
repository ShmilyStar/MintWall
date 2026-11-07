using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SkiaSharp;

namespace MintWall.Services;

public sealed record LibraryMeta(int Width, int Height, long Bytes, string SourceUrl, DateTimeOffset SavedAt);

/// <summary>
/// Sidecar metadata so refreshes never have to re-decode every stored image just to show its size.
/// 桌面/锁屏/每块屏各有一个 LibraryService，但共用同一条进程内缓存，避免各持快照互相覆盖。
/// </summary>
public sealed class LibraryMetaStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly object Gate = new();
    private static readonly Dictionary<string, LibraryMeta> Entries = Load();
    private static bool _dirty;

    public string IdOf(string path) => Path.GetFileNameWithoutExtension(path);

    public bool TryGet(string path, out LibraryMeta meta)
    {
        lock (Gate)
        {
            return Entries.TryGetValue(IdOf(path), out meta!);
        }
    }

    public void Put(string path, int width, int height, string sourceUrl)
    {
        lock (Gate)
        {
            Entries[IdOf(path)] = new LibraryMeta(width, height, SafeLength(path), sourceUrl, DateTimeOffset.Now);
            _dirty = true;
        }
    }

    public void Remove(string path)
    {
        lock (Gate)
        {
            if (Entries.Remove(IdOf(path)))
            {
                _dirty = true;
            }
        }
    }

    /// <summary>Cached metadata, or a one-off header read for files stored before the cache existed.</summary>
    public LibraryMeta Describe(string path)
    {
        lock (Gate)
        {
            if (Entries.TryGetValue(IdOf(path), out LibraryMeta? cached) && cached.Width > 0)
            {
                return cached;
            }
        }

        (int width, int height) = ReadDimensions(path);
        DateTimeOffset stamp;
        try
        {
            stamp = new DateTimeOffset(File.GetLastWriteTime(path));
        }
        catch (IOException)
        {
            stamp = DateTimeOffset.Now;
        }

        LibraryMeta rebuilt = new(width, height, SafeLength(path), string.Empty, stamp);
        if (width > 0)
        {
            lock (Gate)
            {
                Entries[IdOf(path)] = rebuilt;
                _dirty = true;
            }
        }

        return rebuilt;
    }

    /// <summary>批量操作（清库、刷新列表）结束后统一写一次，避免逐条序列化整个文件。</summary>
    public void Flush()
    {
        lock (Gate)
        {
            if (!_dirty)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(DataPaths.Root);
                string temp = MetaFile + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(Entries, Options));
                File.Move(temp, MetaFile, overwrite: true);
                _dirty = false;
            }
            catch (IOException)
            {
                // Metadata is a cache; losing a write must not fail the wallpaper change.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static (int Width, int Height) ReadDimensions(string path)
    {
        try
        {
            // 只读容器头拿宽高，不解码像素；8K 图也不会产生全尺寸位图。
            using SKCodec? codec = SKCodec.Create(path);
            return codec is null ? (0, 0) : (codec.Info.Width, codec.Info.Height);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }

    private static Dictionary<string, LibraryMeta> Load()
    {
        try
        {
            if (File.Exists(DataPaths.SettingsFile) && File.Exists(MetaFile))
            {
                Dictionary<string, LibraryMeta>? loaded = JsonSerializer.Deserialize<Dictionary<string, LibraryMeta>>(File.ReadAllText(MetaFile), Options);
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch
        {
            // A broken cache file simply rebuilds itself on the next download.
        }

        return new Dictionary<string, LibraryMeta>(StringComparer.OrdinalIgnoreCase);
    }

    private static string MetaFile => Path.Combine(DataPaths.Root, "library.json");
}