using System.Windows;
using System.Windows.Controls;
using WallpaperStudio.ViewModels;

namespace WallpaperStudio.Views.Sections;

public partial class SourcesSection : UserControl
{
    public SourcesSection()
    {
        InitializeComponent();
    }

    /// <summary>路径那一栏本身就是入口，点一下等同于「选择目录」。</summary>
    private void SaveFolder_Click(object sender, RoutedEventArgs e) =>
        (DataContext as MonitorWorkspace)?.ChooseSaveFolderCommand.Execute(null);
}
