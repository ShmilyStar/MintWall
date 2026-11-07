using System.Windows.Controls;
using System.Windows.Input;
using WallpaperStudio.ViewModels;

namespace WallpaperStudio.Views;

public partial class LockScreenPage : UserControl
{
    public LockScreenPage() => InitializeComponent();

    private void Preview_Click(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.Lock.Selected is { } workspace)
        {
            workspace.RevealCurrentImageCommand.Execute(null);
        }
    }
}
