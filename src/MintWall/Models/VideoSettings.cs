using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MintWall.Models;

/// <summary>本地视频库里的一条视频（文件已复制进数据目录）。</summary>
public partial class VideoItem : ObservableObject
{
    [ObservableProperty]
    private string _id = Guid.NewGuid().ToString("N");

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private long _sizeBytes;

    [ObservableProperty]
    private bool _isFavorite;

    /// <summary>首帧缩略图，异步生成；没有时界面显示视频图标占位。</summary>
    [ObservableProperty]
    private ImageSource? _thumbnail;

    public string SizeText => SizeBytes >= 1024L * 1024 * 1024
        ? $"{SizeBytes / 1024d / 1024 / 1024:F2} GB"
        : $"{SizeBytes / 1024d / 1024:F1} MB";

    /// <summary>卡片角标用的格式，例如 MP4。</summary>
    public string ExtensionText => Path.GetExtension(FilePath).TrimStart('.').ToUpperInvariant();
}

/// <summary>在线视频源：直链视频，或返回视频地址的 JSON 接口。</summary>
public partial class VideoSource : ObservableObject
{
    [ObservableProperty]
    private string _id = Guid.NewGuid().ToString("N");

    [ObservableProperty]
    private string _name = "新的在线视频源";

    [ObservableProperty]
    private string _url = string.Empty;

    [ObservableProperty]
    private SourceKind _kind = SourceKind.Auto;

    /// <summary>JSON 里视频地址的路径，例如 "data.url"；留空时自动找第一个像视频地址的字符串。</summary>
    [ObservableProperty]
    private string _jsonFieldPath = string.Empty;

    /// <summary>每行一个 "Key: Value" 请求头。</summary>
    [ObservableProperty]
    private string _headers = string.Empty;

    /// <summary>开着时抓到的视频存进保存位置；关着则只放缓存，换下一个时清掉。</summary>
    [ObservableProperty]
    private bool _autoSave;

    [ObservableProperty]
    private bool _enabled = true;
}

/// <summary>一块屏的视频壁纸配置：本地库、在线库、启用开关与最近播放。</summary>
public sealed class VideoLibrarySettings
{
    public bool LocalEnabled { get; set; } = true;

    public bool OnlineEnabled { get; set; } = true;

    /// <summary>用户自己选的视频库目录；留空则用应用内置的按屏目录（与壁纸库一致）。</summary>
    public string LibraryDirectory { get; set; } = string.Empty;

    /// <summary>收藏的视频文件路径（目录驱动下用路径当稳定标识）。</summary>
    public List<string> FavoritePaths { get; set; } = new();

    /// <summary>自动保存的视频存放目录；留空则用内置按屏归档目录（与壁纸源的保存位置一致）。</summary>
    public string SaveDirectory { get; set; } = string.Empty;

    public List<VideoSource> Sources { get; set; } = new();

    public string LastPlayed { get; set; } = string.Empty;
}
