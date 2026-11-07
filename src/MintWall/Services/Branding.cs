using System.Globalization;

namespace MintWall.Services;

/// <summary>显示名跟系统语言走：中文系统用中文名，其他语言统一用英文名，两个名字不并排出现。</summary>
public static class Branding
{
    public static bool IsChinese =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);

    public static string DisplayName => IsChinese ? "薄荷壁纸" : "MintWall";
}
