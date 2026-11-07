using System.IO;
using System.Security.Cryptography;
using System.Text;
using SkiaSharp;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WallpaperStudio.Services;

public static class ThumbnailStore
{
    public const int MaxEdge = 360;

    public const int PreviewEdge = 1280;

    public static string ThumbPathFor(string sourcePath, int maxEdge = MaxEdge)
    {
        long stamp = 0;
        try
        {
            stamp = new FileInfo(sourcePath).LastWriteTimeUtc.Ticks;
        }
        catch (IOException)
        {
            // A missing file hashes to the zero stamp; the caller will fail to load it anyway.
        }

        string key = sourcePath.ToLowerInvariant() + "|" + maxEdge + "|" + stamp;
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(key));
        return Path.Combine(DataPaths.ThumbDir, Convert.ToHexString(hash) + ".png");
    }

    /// <summary>Renders a PNG preview through Skia so the grid never decodes full-size images in WPF.</summary>
    public static string Ensure(string sourcePath, int maxEdge = MaxEdge)
    {
        string thumb = ThumbPathFor(sourcePath, maxEdge);
        if (File.Exists(thumb))
        {
            return thumb;
        }

        Directory.CreateDirectory(DataPaths.ThumbDir);

        using SKBitmap? full = SKBitmap.Decode(sourcePath);
        if (full is null)
        {
            return thumb;
        }

        int edge = Math.Max(full.Width, full.Height);
        SKBitmap? resized = null;
        SKBitmap target = full;
        if (edge > maxEdge)
        {
            double scale = maxEdge / (double)edge;
            SKImageInfo info = new(Math.Max(1, (int)(full.Width * scale)), Math.Max(1, (int)(full.Height * scale)));
            resized = full.Resize(info, SKFilterQuality.Medium);
            if (resized is not null)
            {
                target = resized;
            }
        }

        using SKImage image = SKImage.FromBitmap(target);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("缩略图编码失败。");

        string temp = thumb + ".tmp";
        using (FileStream stream = File.Create(temp))
        {
            data.SaveTo(stream);
        }

        resized?.Dispose();
        if (File.Exists(thumb))
        {
            File.Delete(thumb);
        }

        File.Move(temp, thumb);
        return thumb;
    }

    public static ImageSource? TryLoad(string sourcePath, int maxEdge = MaxEdge)
    {
        try
        {
            string thumb = Ensure(sourcePath, maxEdge);
            if (!File.Exists(thumb))
            {
                return null;
            }

            BitmapImage image = new();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(thumb, UriKind.Absolute);
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
