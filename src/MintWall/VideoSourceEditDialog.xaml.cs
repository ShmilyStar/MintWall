using System.Windows;
using MintWall.Models;

namespace MintWall;

/// <summary>在线视频源的添加/编辑弹窗，版式与壁纸源设置一致。</summary>
public partial class VideoSourceEditDialog : Window
{
    public VideoSourceEditDialog(VideoSource draft, bool isNew)
    {
        InitializeComponent();
        Draft = draft;
        DataContext = draft;
        Title = isNew ? "添加在线视频源" : "编辑在线视频源";
    }

    public VideoSource Draft { get; }

    public IReadOnlyList<KeyValuePair<SourceKind, string>> KindOptions { get; } = new[]
    {
        new KeyValuePair<SourceKind, string>(SourceKind.Auto, "自动判断"),
        new KeyValuePair<SourceKind, string>(SourceKind.DirectImage, "直接是视频"),
        new KeyValuePair<SourceKind, string>(SourceKind.Json, "JSON 里取链接"),
    };

    public static bool Show(Window? owner, VideoSource draft, bool isNew)
    {
        VideoSourceEditDialog dialog = new(draft, isNew)
        {
            Owner = owner ?? Application.Current?.MainWindow,
        };
        return dialog.ShowDialog() == true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        string url = (Draft.Url ?? string.Empty).Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            AppDialog.Show(this, "地址不合法", "请填写完整的 http(s) 视频地址。", "知道了");
            return;
        }

        Draft.Url = url;
        Draft.Name = string.IsNullOrWhiteSpace(Draft.Name) ? "在线视频源" : Draft.Name.Trim();
        DialogResult = true;
    }
}
