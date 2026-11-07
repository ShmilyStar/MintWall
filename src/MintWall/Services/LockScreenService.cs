using System.IO;
using Windows.Storage;
using Windows.System.UserProfile;

namespace MintWall.Services;

public sealed record LockScreenOutcome(bool Success, string Message, string? AppliedPath);

/// <summary>
/// Lock screen background, through Windows.System.UserProfile.LockScreen.
/// Why this and not UserProfilePersonalizationSettings.TrySetLockScreenImageAsync: measured on a real
/// Windows 10 desktop, that newer call returns false for a loose full-trust process, while the older
/// SetImageFileAsync writes and reads back correctly. Packaged builds keep working because the wallpaper
/// folder lives inside the package's own LocalAppData.
/// </summary>
public static class LockScreenService
{
    private static readonly string[] SupportedExtensions = { ".jpg", ".jpeg", ".png" };

    public static string? ApiError { get; private set; }

    public static bool IsApiAvailable => ApiError is null;

    /// <summary>The file Windows currently shows on the lock screen, or null when it cannot be read.</summary>
    public static string? ReadCurrentPath()
    {
        try
        {
            Uri uri = LockScreen.OriginalImageFile;
            return uri is null || !uri.IsAbsoluteUri ? null : LocalPath(uri);
        }
        catch (Exception ex)
        {
            ApiError = Describe(ex);
            return null;
        }
    }

    public static async Task<LockScreenOutcome> ApplyAsync(string imagePath, CancellationToken ct)
    {
        string path = Path.GetFullPath(imagePath);

        if (!File.Exists(path))
        {
            return new LockScreenOutcome(false, $"文件不存在：{path}", null);
        }

        if (!SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
        {
            return new LockScreenOutcome(false,
                $"锁屏只接受 JPG / PNG，当前是 {Path.GetExtension(path)}。请先在“设置”里把入库格式改成 JPG 或 PNG。", null);
        }

        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(path).AsTask(ct).ConfigureAwait(false);
            await LockScreen.SetImageFileAsync(file).AsTask(ct).ConfigureAwait(false);

            string? applied = ReadCurrentPath();
            if (applied is null)
            {
                return new LockScreenOutcome(true, "锁屏已写入，但系统没有回读出图片路径。", path);
            }

            bool matches = string.Equals(applied, path, StringComparison.OrdinalIgnoreCase);
            return matches
                ? new LockScreenOutcome(true, "锁屏壁纸已更新。", applied)
                : new LockScreenOutcome(false,
                    $"系统仍指向另一张图：{applied}。常见原因是锁屏被“Windows 聚焦”、第三方锁屏应用或组策略接管。", applied);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ApiError = Describe(ex);
            return new LockScreenOutcome(false, "锁屏接口调用失败：" + ApiError, null);
        }
    }

    private static string LocalPath(Uri uri)
    {
        try
        {
            return uri.Scheme == Uri.UriSchemeFile ? uri.LocalPath : uri.AbsoluteUri;
        }
        catch (UriFormatException)
        {
            return uri.OriginalString;
        }
    }

    private static string Describe(Exception ex)
    {
        Exception root = ex;
        while (root.InnerException is not null)
        {
            root = root.InnerException;
        }

        return $"{root.GetType().Name} 0x{root.HResult:x8}: {root.Message}";
    }
}
