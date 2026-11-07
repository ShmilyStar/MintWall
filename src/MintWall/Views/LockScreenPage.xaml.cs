using System.Windows.Controls;
using System.Windows.Input;
using MintWall.ViewModels;

namespace MintWall.Views;

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
