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
    }

    private void EnsurePreview() => (DataContext as MonitorWorkspace)?.EnsureSourcePreview();

    /// <summary>路径那一栏本身就是入口，点一下等同于「选择目录」。</summary>
    private void SaveFolder_Click(object sender, RoutedEventArgs e) =>
        (DataContext as MonitorWorkspace)?.ChooseSaveFolderCommand.Execute(null);
}
