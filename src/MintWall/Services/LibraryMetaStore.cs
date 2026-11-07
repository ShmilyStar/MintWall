using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SkiaSharp;

namespace MintWall.Services;

public sealed record LibraryMeta(int Width, int Height, long Bytes, string SourceUrl, DateTimeOffset SavedAt);

/// <summary>
/// Sidecar metadata so refreshes never have to re-decode every stored image just to show its size.
/// </summary>
public sealed class LibraryMetaStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Dictionary<string, LibraryMeta> _entries;

    public LibraryMetaStore()
    {
        _entries = Load();
    }

    public string IdOf(string path) => Path.GetFileNameWithoutExtension(path);

    public bool TryGet(string path, out LibraryMeta meta) => _entries.TryGetValue(IdOf(path), out meta!);

    public void Put(string path, int width, int height, string sourceUrl)
    {
        _entries[IdOf(path)] = new LibraryMeta(width, height, SafeLength(path), sourceUrl, DateTimeOffset.Now);
        Save();
    }

    public void Remove(string path)
    {
        if (_entries.Remove(IdOf(path)))
        {
            Save();
        }
    }

    /// <summary>Cached metadata, or a one-off read for files stored before the cache existed.</summary>
    public LibraryMeta Describe(string path)
    {
        if (_entries.TryGetValue(IdOf(path), out LibraryMeta? cached) && cached.Width > 0)
        {
            return cached;
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
            _entries[IdOf(path)] = rebuilt;
            Save();
        }

        return rebuilt;
    }

    private static (int Width, int Height) ReadDimensions(string path)
    {
        try
        {
            using SKBitmap? bitmap = SKBitmap.Decode(path);
            return bitmap is null ? (0, 0) : (bitmap.Width, bitmap.Height);
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

    private Dictionary<string, LibraryMeta> Load()
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

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(DataPaths.Root);
            File.WriteAllText(MetaFile, JsonSerializer.Serialize(_entries, Options));
        }
        catch
        {
            // Metadata is a cache; losing a write must not fail the wallpaper change.
        }
    }

    private static string MetaFile => Path.Combine(DataPaths.Root, "library.json");
}
