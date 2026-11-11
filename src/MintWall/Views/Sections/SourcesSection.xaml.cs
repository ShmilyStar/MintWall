using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MintWall.ViewModels;

namespace MintWall.Views.Sections;

public partial class SourcesSection : UserControl
{
    public SourcesSection() => InitializeComponent();

    /// <summary>路径那一栏本身就是入口，点一下等同于「选择目录」。</summary>
    private void SaveFolder_Click(object sender, RoutedEventArgs e) =>
        (DataContext as MonitorWorkspace)?.ChooseSaveFolderCommand.Execute(null);

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
            && list.DataContext is MonitorWorkspace monitor)
        {
            monitor.EditSourceCommand.Execute(null);
        }
    }

    private void CopySourceLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Parent: ContextMenu { PlacementTarget: ListBox list } }
            && list.DataContext is MonitorWorkspace monitor
            && monitor.SelectedSource is { } source)
        {
            Clipboard.SetText(source.Url ?? string.Empty);
        }
    }
}
