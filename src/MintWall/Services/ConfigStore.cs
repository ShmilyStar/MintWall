using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MintWall.Models;

namespace MintWall.Services;

public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(DataPaths.SettingsFile))
            {
                string text = File.ReadAllText(DataPaths.SettingsFile);
                AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(text, Options);

                if (loaded is not null && (loaded.Desktop.IsSplit || loaded.Lock.IsSplit || HasLegacyPayload(loaded)))
                {
                    return loaded;
                }

                // 1.0 的 settings.json 只有一套源/定时/样式，升级时整体搬到“桌面”，锁屏从空开始。
                LegacySettings? legacy = JsonSerializer.Deserialize<LegacySettings>(text, Options);
                if (legacy is not null)
                {
                    return Upgrade(legacy);
                }
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }

        AppSettings fresh = New();
        Save(fresh);
        return fresh;
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(DataPaths.Root);
        File.WriteAllText(DataPaths.SettingsFile, JsonSerializer.Serialize(settings, Options));
    }

    public string? LastError { get; private set; }

    /// <summary>新装时的默认配置：不给任何屏幕预建配置，等识别到显示器后再补。</summary>
    public static AppSettings New()
    {
        AppSettings settings = new();
        settings.Desktop.SyncLockScreen = true;
        return settings;
    }

    /// <summary>第一次给桌面建配置时附带的示例源。</summary>
    public static SourceProfile SeedSource() => new()
    {
        Name = "t.alcy.cc / ycy",
        Url = "https://t.alcy.cc/ycy",
        Kind = SourceKind.Auto,
    };

    private static bool HasLegacyPayload(AppSettings settings) =>
        settings.Desktop.LegacySources is { Count: > 0 } || settings.Lock.LegacySources is { Count: > 0 };

    private static AppSettings Upgrade(LegacySettings legacy)
    {
        AppSettings settings = new();

        settings.Desktop.LegacySources = legacy.Sources.Count > 0 ? legacy.Sources : new List<SourceProfile> { SeedSource() };
        settings.Desktop.LegacyActiveSourceId = legacy.ActiveSourceId ?? settings.Desktop.LegacySources[0].Id;
        settings.Desktop.LegacySchedule = new ScheduleSettings
        {
            Enabled = legacy.Schedule.Enabled,
            IntervalMinutes = legacy.Schedule.IntervalMinutes,
        };
        settings.Desktop.LegacyStyle = legacy.Style;
        settings.Desktop.LegacyLastApplied = legacy.LockScreen.LastApplied;
        settings.Desktop.SyncLockScreen = legacy.LockScreen.SyncWithDesktop;
        settings.Lock.LegacyLastApplied = legacy.LockScreen.LastApplied;

        settings.Encode = legacy.Encode;
        settings.Favorites = legacy.Favorites;
        settings.LibraryLimit = legacy.LibraryLimit;
        settings.LastPage = legacy.LastPage;

        return settings;
    }

    /// <summary>只用于读旧文件，字段名与 1.0 的 settings.json 一致。</summary>
    private sealed class LegacySettings
    {
        public List<SourceProfile> Sources { get; set; } = new();

        public string? ActiveSourceId { get; set; }

        public LegacySchedule Schedule { get; set; } = new();

        public LegacyLock LockScreen { get; set; } = new();

        public WallpaperStyleMode Style { get; set; } = WallpaperStyleMode.Fill;

        public EncodePreference Encode { get; set; } = EncodePreference.Auto;

        public List<string> Favorites { get; set; } = new();

        public int LibraryLimit { get; set; } = 300;

        public string LastPage { get; set; } = "home";
    }

    private sealed class LegacySchedule
    {
        public bool Enabled { get; set; }

        public int IntervalMinutes { get; set; } = 30;

        public string TargetMonitorId { get; set; } = Wallpapers.AllMonitors;
    }

    private sealed class LegacyLock
    {
        public bool SyncWithDesktop { get; set; } = true;

        public string LastApplied { get; set; } = string.Empty;
    }
}
