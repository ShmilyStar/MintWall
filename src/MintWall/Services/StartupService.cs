using Microsoft.Win32;

namespace MintWall.Services;

/// <summary>开机自启写在当前用户的 Run 项里，不需要管理员权限，卸载/关闭时只删自己的值。</summary>
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MintWall";
    private const string LegacyValueName = "WallpaperStudio";

    /// <summary>改名前的自启项指向旧 exe，启动时顺手清掉。</summary>
    public static void RemoveLegacyValue()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(LegacyValueName, throwOnMissingValue: false);
    }

    public static bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string value && value.Length > 0;
    }

    public static void SetEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey);

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        string? exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            throw new InvalidOperationException("读不到程序自身路径。");
        }

        key.SetValue(ValueName, $"\"{exe}\"");
    }
}
