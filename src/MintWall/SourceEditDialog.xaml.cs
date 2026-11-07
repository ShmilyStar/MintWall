using System.Windows;
using MintWall.Models;
using MintWall.Services;

namespace MintWall;

public partial class SourceEditDialog : Window
{
    public SourceEditDialog(SourceProfile draft, bool isNew)
    {
        InitializeComponent();
        Draft = draft;
        DataContext = draft;
        Title = isNew ? "添加壁纸源" : "编辑壁纸源";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    public SourceProfile Draft { get; }

    public IReadOnlyList<KeyValuePair<SourceKind, string>> KindOptions { get; } = new[]
    {
        new KeyValuePair<SourceKind, string>(SourceKind.Auto, "自动判断"),
        new KeyValuePair<SourceKind, string>(SourceKind.DirectImage, "直接是图片"),
        new KeyValuePair<SourceKind, string>(SourceKind.Json, "JSON 里取链接"),
        new KeyValuePair<SourceKind, string>(SourceKind.Bing, "必应每日壁纸"),
        new KeyValuePair<SourceKind, string>(SourceKind.Spotlight, "Windows 聚焦（本机）"),
    };

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Draft.Kind == SourceKind.Bing)
        {
            Draft.Url = BingService.ArchiveUrl;
            Draft.Name = string.IsNullOrWhiteSpace(Draft.Name) ? "必应每日壁纸" : Draft.Name.Trim();
            DialogResult = true;
            return;
        }

        if (Draft.Kind == SourceKind.Spotlight)
        {
            Draft.Url = SpotlightService.SourceUrl;
            Draft.Name = string.IsNullOrWhiteSpace(Draft.Name) ? "Windows 聚焦" : Draft.Name.Trim();
            DialogResult = true;
            return;
        }

        string url = (Draft.Url ?? string.Empty).Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            MessageBox.Show(this, "请填写完整的 http(s) 地址。", "地址不合法", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Draft.Url = url;
        Draft.Name = string.IsNullOrWhiteSpace(Draft.Name) ? parsed.Host : Draft.Name.Trim();
        DialogResult = true;
    }
}
