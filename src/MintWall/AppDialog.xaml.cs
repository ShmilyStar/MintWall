using System.Windows;

namespace MintWall;

/// <summary>与软件风格一致的自绘提示框：Esc / 关闭按钮走次要动作，Enter 走主按钮。</summary>
public partial class AppDialog : Window
{
    public AppDialog(string title, string message, string primaryText, string? secondaryText)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        PrimaryButton.Content = primaryText;

        if (string.IsNullOrWhiteSpace(secondaryText))
        {
            SecondaryButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            SecondaryButton.Content = secondaryText;
        }
    }

    public bool PrimaryChosen { get; private set; }

    public static bool Show(string title, string message, string primaryText, string? secondaryText = null) =>
        Show(null, title, message, primaryText, secondaryText);

    public static bool Show(Window? owner, string title, string message, string primaryText, string? secondaryText = null)
    {
        AppDialog dialog = new(title, message, primaryText, secondaryText)
        {
            Owner = owner ?? Application.Current?.MainWindow,
        };
        dialog.ShowDialog();
        return dialog.PrimaryChosen;
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        PrimaryChosen = true;
        Close();
    }

    private void Secondary_Click(object sender, RoutedEventArgs e) => Close();
}
