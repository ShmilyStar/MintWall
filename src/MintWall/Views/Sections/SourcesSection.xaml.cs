using System.Windows;
using System.Windows.Controls;
using MintWall.ViewModels;

namespace MintWall.Views.Sections;

public partial class SourcesSection : UserControl
{
    public SourcesSection()
    {
        InitializeComponent();
        Loaded += (_, _) => EnsurePreview();
        DataContextChanged += (_, _) => EnsurePreview();
        IsVisibleChanged += (_, _) => EnsurePreview();
    }

    // 页面用 Visibility 切换，Collapsed 时 Loaded 照样会触发；不可见就不该提前联网取样片。
    private void EnsurePreview()
    {
        if (IsVisible)
        {
            (DataContext as MonitorWorkspace)?.EnsureSourcePreview();
        }
    }

    /// <summary>路径那一栏本身就是入口，点一下等同于「选择目录」。</summary>
    private void SaveFolder_Click(object sender, RoutedEventArgs e) =>
        (DataContext as MonitorWorkspace)?.ChooseSaveFolderCommand.Execute(null);
}
