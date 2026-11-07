using CommunityToolkit.Mvvm.ComponentModel;

namespace MintWall.Models;

public abstract partial class NavItem : ObservableObject
{
    public bool IsGroup => this is NavGroup;

    [ObservableProperty]
    private string _label = string.Empty;
}

public sealed partial class NavGroup : NavItem
{
}

public sealed partial class NavLink : NavItem
{
    public required string Key { get; init; }

    [ObservableProperty]
    private string _badge = string.Empty;

    /// <summary>字形按 key 取，避免把私有区字符直接写进源码。</summary>
    public string Glyph => NavGlyphs.For(Key);
}

internal static class NavGlyphs
{
    private static readonly Dictionary<string, int> Map = new(StringComparer.Ordinal)
    {
        ["home"] = 0xE80F,
        ["desktop"] = 0xE7F4,
        ["lock"] = 0xE72E,
        ["desktoplib"] = 0xE8B9,
        ["locklib"] = 0xE91B,
        ["desktopsrc"] = 0xE774,
        ["locksrc"] = 0xE753,
        ["settings"] = 0xE713,
        ["diagnostics"] = 0xE9D9,
    };

    public static string For(string key) =>
        Map.TryGetValue(key, out int code) ? char.ConvertFromUtf32(code) : "?";
}
