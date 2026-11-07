using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MintWall.Models;

public partial class LibraryItem : ObservableObject
{
    public required string Id { get; init; }

    public required string FilePath { get; init; }

    public string? SourceUrl { get; init; }

    public long Bytes { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    [ObservableProperty]
    private bool _isFavorite;

    [ObservableProperty]
    private ImageSource? _thumbnail;

    public string FileName => Path.GetFileName(FilePath);

    public string Dimensions => Width > 0 ? $"{Width} × {Height}" : "未知";

    public string SizeText => Bytes <= 0
        ? "未知"
        : Bytes >= 1024 * 1024
            ? $"{Bytes / 1024.0 / 1024.0:0.0} MB"
            : $"{Bytes / 1024.0:0} KB";

    public override string ToString() => FileName;
}

public sealed class MonitorInfo
{
    public required string Id { get; init; }

    public required int Index { get; init; }

    public required string Label { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public string CurrentWallpaper { get; set; } = string.Empty;

    public bool PerMonitorSupported { get; init; } = true;

    public string ApplyTarget => PerMonitorSupported ? Id : Wallpapers.AllMonitors;

    public string SizeText => Width > 0 && Height > 0 ? $"{Width} × {Height}" : "未识别";

    public string SupportHint => PerMonitorSupported
        ? "可以单独给这块屏设置壁纸。"
        : "这台系统只能整体设置，这块屏会跟随“所有显示器”。";

    public override string ToString() => $"{Label}  {SizeText}";
}
