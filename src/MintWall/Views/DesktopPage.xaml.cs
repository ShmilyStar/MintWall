using System.Windows.Controls;
using System.Windows.Input;
using MintWall.ViewModels;

namespace MintWall.Views;

public partial class DesktopPage : UserControl
{
    public DesktopPage()
    {
        InitializeComponent();
    }

    private void Preview_Click(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.Desktop.Selected is { } workspace)
        {
            workspace.RevealCurrentImageCommand.Execute(null);
        }
    }
}
