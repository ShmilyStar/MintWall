using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WallpaperStudio.Models;
using WallpaperStudio.ViewModels;

namespace WallpaperStudio.Views.Sections;

public partial class LibrarySection : UserControl
{
    /// <summary>缩略图区域最大高度；独立壁纸库页把它放宽以铺满整页。</summary>
    public static readonly DependencyProperty GalleryMaxHeightProperty =
        DependencyProperty.Register(nameof(GalleryMaxHeight), typeof(double), typeof(LibrarySection),
            new PropertyMetadata(460d));

    public double GalleryMaxHeight
    {
        get => (double)GetValue(GalleryMaxHeightProperty);
        set => SetValue(GalleryMaxHeightProperty, value);
    }

    public LibrarySection()
    {
        InitializeComponent();
    }

    /// <summary>双击缩略图直接应用到本目标。</summary>
    private void Library_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MonitorWorkspace workspace && grid.SelectedItem is LibraryItem item)
        {
            workspace.ApplyItemCommand.Execute(item);
        }
    }
}
