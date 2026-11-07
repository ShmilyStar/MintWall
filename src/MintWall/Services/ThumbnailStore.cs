using System.IO;
using System.Security.Cryptography;
using System.Text;
using SkiaSharp;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MintWall.Services;

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

        using SKCodec? codec = SKCodec.Create(sourcePath);
        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0)
        {
            return thumb;
        }

        // 先按编解码器支持的采样率缩小解码，避免 8K 原图整张进内存再缩。
        SKImageInfo info = codec.Info;
        int edge = Math.Max(info.Width, info.Height);
        if (edge > maxEdge)
        {
            SKSizeI scaled = codec.GetScaledDimensions((float)maxEdge / edge);
            info = new SKImageInfo(scaled.Width, scaled.Height, info.ColorType, info.AlphaType);
        }

        using SKBitmap bitmap = new(info);
        SKCodecResult result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            return thumb;
        }

        SKBitmap target = bitmap;
        SKBitmap? resized = null;
        int decodedEdge = Math.Max(bitmap.Width, bitmap.Height);
        if (decodedEdge > maxEdge)
        {
            double scale = maxEdge / (double)decodedEdge;
            SKImageInfo small = new(Math.Max(1, (int)(bitmap.Width * scale)), Math.Max(1, (int)(bitmap.Height * scale)));
            resized = bitmap.Resize(small, SKFilterQuality.Medium);
            if (resized is not null)
            {
                target = resized;
            }
        }

        using SKImage image = SKImage.FromBitmap(target);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("缩略图编码失败。");

        // 网格(360)和预览(1280)可能同时给同一张图生成缩略图，临时名必须互不冲突。
        string temp = thumb + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        using (FileStream stream = File.Create(temp))
        {
            data.SaveTo(stream);
        }

        resized?.Dispose();
        File.Move(temp, thumb, overwrite: true);
        return thumb;
    }

    /// <summary>源文件更新或预览换档后旧 PNG 成孤儿；定期清掉长期没人碰的，避免 thumbs 只增不减。</summary>
    public static void PruneOld(TimeSpan maxAge)
    {
        try
        {
            if (!Directory.Exists(DataPaths.ThumbDir))
            {
                return;
            }

            DateTime cutoff = DateTime.UtcNow - maxAge;
            foreach (string file in Directory.EnumerateFiles(DataPaths.ThumbDir))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
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
