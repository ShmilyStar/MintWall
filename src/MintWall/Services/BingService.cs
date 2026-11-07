using System.Text.Json;
using MintWall.Models;

namespace MintWall.Services;

/// <summary>必应每日壁纸：先请求归档接口，再按返回的图片地址取图。</summary>
public static class BingService
{
    public const string ArchiveUrl = "https://www.bing.com/HPImageArchive.aspx?format=js&idx=0&n=1&mkt=zh-CN";

    public static SourceProfile BuiltInSource() => new()
    {
        Id = "bing",
        Name = "必应每日壁纸",
        Url = ArchiveUrl,
        Kind = SourceKind.Bing,
    };

    /// <summary>从归档 JSON 里读出当天图片地址；UHD 可能不存在，取不到时只用标准尺寸。</summary>
    public static (string Standard, string? Uhd)? ReadImageUrls(byte[] json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("images", out JsonElement images)
            || images.ValueKind != JsonValueKind.Array
            || images.GetArrayLength() == 0)
        {
            return null;
        }

        JsonElement first = images[0];
        string? url = first.TryGetProperty("url", out JsonElement urlNode) ? urlNode.GetString() : null;
        string? urlBase = first.TryGetProperty("urlbase", out JsonElement baseNode) ? baseNode.GetString() : null;

        string? standard = Normalize(url ?? (string.IsNullOrWhiteSpace(urlBase) ? null : urlBase + "_1920x1080.jpg"));
        if (standard is null)
        {
            return null;
        }

        string? uhd = Normalize(string.IsNullOrWhiteSpace(urlBase) ? null : urlBase + "_UHD.jpg");
        return (standard, uhd);
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return "https:" + trimmed;
        }

        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return "https://www.bing.com" + (trimmed.StartsWith('/') ? trimmed : "/" + trimmed);
    }
}
