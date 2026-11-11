namespace MintWall.Models;

public enum SourceKind
{
    /// <summary>Inspect the response and decide (content-type image, or JSON holding an image URL).</summary>
    Auto,

    /// <summary>The URL body is the image itself (follows redirects).</summary>
    DirectImage,

    /// <summary>The URL returns JSON; an image URL is read from it and fetched.</summary>
    Json,

    /// <summary>不联网，直接用系统本机的 Windows 聚焦图（旧配置兼容，界面已不再提供该选项）。</summary>
    Spotlight,

    /// <summary>必应每日壁纸：从 Bing 归档接口取当天的图。</summary>
    Bing,
}

public enum WallpaperStyleMode
{
    Fill,
    Fit,
    Stretch,
    Center,
    Tile,
    Span,
}

public enum EncodePreference
{
    Auto,
    Jpeg,
    Png,
}
