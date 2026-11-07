using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MintWall.Models;

public partial class SourceProfile : ObservableObject
{
    [ObservableProperty]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    private string _id = Guid.NewGuid().ToString("N");

    [ObservableProperty]
    private string _name = "新的壁纸源";

    [ObservableProperty]
    private string _url = "https://t.alcy.cc/ycy";

    [ObservableProperty]
    private SourceKind _kind = SourceKind.Auto;

    /// <summary>Dot separated path into a JSON body, e.g. "data.url". Empty means auto-detect.</summary>
    [ObservableProperty]
    private string _jsonFieldPath = string.Empty;

    /// <summary>One "Key: Value" header per line.</summary>
    [ObservableProperty]
    private string _headers = string.Empty;

    /// <summary>开着时抓到的图存进这块屏的“保存位置”；关着则只进缓存，换下一张就删。</summary>
    [ObservableProperty]
    private bool _autoSave;

    /// <summary>停用后“换一张”和定时器会跳过这个源，手动测试与预览仍然可用。</summary>
    [ObservableProperty]
    private bool _enabled = true;

    public SourceProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        Url = Url,
        Kind = Kind,
        JsonFieldPath = JsonFieldPath,
        Headers = Headers,
        AutoSave = AutoSave,
        Enabled = Enabled,
    };

    public override string ToString() => Name;
}

public sealed class ScheduleSettings
{
    public bool Enabled { get; set; }

    public int IntervalMinutes { get; set; } = 30;
}

/// <summary>
/// 一块屏幕的完整配置：自己的源列表、壁纸库目录、保存位置、定时与样式。
/// </summary>
public sealed class MonitorSettings
{
    /// <summary>显示器标识（IDesktopWallpaper 设备路径或稳定的 win32 序号名），内置壁纸库目录按它派生。</summary>
    public string MonitorId { get; set; } = Wallpapers.AllMonitors;

    /// <summary>最后一次识别到的名字，屏幕拔掉后仍能看到这是哪块屏。</summary>
    public string Label { get; set; } = "显示器 1";

    /// <summary>用户自己选的壁纸库目录；留空则用应用内置的按屏目录。</summary>
    public string LibraryDirectory { get; set; } = string.Empty;

    /// <summary>开了自动保存的源抓到的图存到这里；留空则用应用内置的按屏归档目录，不算进壁纸库。</summary>
    public string SaveDirectory { get; set; } = string.Empty;

    public List<SourceProfile> Sources { get; set; } = new();

    public string? ActiveSourceId { get; set; }

    public ScheduleSettings Schedule { get; set; } = new();

    /// <summary>“换一张”和定时器可以从哪些来源取图；可以同时开多个，按顺序轮换。</summary>
    public bool FetchFromSources { get; set; } = true;

    public bool FetchFromLibrary { get; set; }

    public bool FetchFromBuiltIn { get; set; }

    /// <summary>仅桌面用；锁屏由系统自行裁切。</summary>
    public WallpaperStyleMode Style { get; set; } = WallpaperStyleMode.Fill;

    public string LastApplied { get; set; } = string.Empty;
}

/// <summary>
/// 桌面与锁屏各自一份；每块显示器在 Monitors 里再单独存一份源、库、定时与样式。
/// </summary>
public sealed class TargetSettings
{
    public List<MonitorSettings> Monitors { get; set; } = new();

    /// <summary>仅桌面用：每次写桌面顺带写锁屏。</summary>
    public bool SyncLockScreen { get; set; }

    [JsonIgnore]
    public bool IsSplit => Monitors.Count > 0;

    // 以下对应“还没按显示器拆分”的旧配置，只用于读老 settings.json；迁移后置空、不再写盘。
    [JsonPropertyName("sources")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SourceProfile>? LegacySources { get; set; }

    [JsonPropertyName("activeSourceId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyActiveSourceId { get; set; }

    [JsonPropertyName("schedule")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScheduleSettings? LegacySchedule { get; set; }

    [JsonPropertyName("style")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WallpaperStyleMode? LegacyStyle { get; set; }

    [JsonPropertyName("lastApplied")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyLastApplied { get; set; }
}

public sealed class AppSettings
{
    public TargetSettings Desktop { get; set; } = new();

    public TargetSettings Lock { get; set; } = new();

    public EncodePreference Encode { get; set; } = EncodePreference.Auto;

    /// <summary>设置页里的桌面填充方式，改动时同步到所有桌面显示器。</summary>
    public WallpaperStyleMode DesktopStyle { get; set; } = WallpaperStyleMode.Fill;

    public List<string> Favorites { get; set; } = new();

    public int LibraryLimit { get; set; } = 300;

    public string LastPage { get; set; } = "home";

    public bool StartWithWindows { get; set; }

    /// <summary>老壁纸库的散图已经搬进各屏目录，避免每次启动重复搬。</summary>
    public bool LibrarySplitDone { get; set; }

    public TargetSettings For(bool lockScreen) => lockScreen ? Lock : Desktop;
}
