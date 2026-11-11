using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MintWall.Services;
using MintWall.ViewModels;

namespace MintWall.Views;

public partial class VideoSourcesPage : UserControl
{
    public VideoSourcesPage() => InitializeComponent();

    private void PreviewMedia_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not MediaElement media)
        {
            return;
        }

        long size = 0;
        try
        {
            if (media.Source is { IsFile: true })
            {
                size = new FileInfo(media.Source.LocalPath).Length;
            }
        }
        catch (IOException)
        {
        }

        PreviewResolution.Text = $"{media.NaturalVideoWidth} × {media.NaturalVideoHeight}";
        PreviewSize.Text = size > 0 ? ImageService.FormatSize(size) : string.Empty;
    }

    private void PreviewMedia_Ended(object sender, RoutedEventArgs e)
    {
        if (sender is MediaElement media)
        {
            media.Position = TimeSpan.Zero;
            media.Play();
        }
    }

    private void PreviewMedia_Failed(object sender, ExceptionRoutedEventArgs e)
    {
        if (DataContext is MainViewModel host)
        {
            host.StatusText = "预览失败：" + (e.ErrorException?.Message ?? "这个视频无法解码。");
        }
    }

    /// <summary>右键“复制地址”：拷走选中源的接口地址。</summary>
    private void CopyAddress_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement target } }
            && target.DataContext is VideoSectionWorkspace section
            && section.SelectedSource is { } source)
        {
            Clipboard.SetText(source.Url ?? string.Empty);
        }
    }

    /// <summary>右键先选中鼠标下的源，再弹“编辑 / 复制”。</summary>
    private void SourcesList_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject origin
            && ItemsControl.ContainerFromElement((ListBox)sender, origin) is ListBoxItem item)
        {
            item.IsSelected = true;
        }
    }

    private void EditSourceMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Parent: ContextMenu { PlacementTarget: ListBox list } }
            && list.DataContext is VideoSectionWorkspace section)
        {
            section.EditSourceCommand.Execute(null);
        }
    }

    private void CopySourceLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Parent: ContextMenu { PlacementTarget: ListBox list } }
            && list.DataContext is VideoSectionWorkspace section
            && section.SelectedSource is { } source)
        {
            Clipboard.SetText(source.Url ?? string.Empty);
        }
    }
}
