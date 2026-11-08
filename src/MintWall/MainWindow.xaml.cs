using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using MintWall.Models;
using MintWall.Services;
using MintWall.ViewModels;

namespace MintWall;

public partial class MainWindow : Window
{
    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp" };

    private readonly MainViewModel _viewModel = new();

    private bool _exiting;
    private bool _decidingClose;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        AllowDrop = true;
        Drop += OnFileDrop;
        DragOver += OnFileDragOver;
        SourceInitialized += (_, _) => WindowTheme.EnableCustomChrome(this);
        StateChanged += (_, _) => UpdateMaximizeGlyph();
        _viewModel.EditSourceRequested += OpenSourceEditor;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void ToggleMaximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_exiting)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        if (!_decidingClose)
        {
            _decidingClose = true;
            _ = CloseOrHideAsync();
        }
    }

    private async Task CloseOrHideAsync()
    {
        // IsEnabled 内部是 WinRT 同步等待，放在 UI 线程上会和 dispatcher 续延死锁。
        bool startup;
        try
        {
            startup = await Task.Run(StartupService.IsEnabled);
        }
        finally
        {
            _decidingClose = false;
        }

        if (startup)
        {
            Hide();
            return;
        }

        _exiting = true;
        Close();
    }

    private void UpdateMaximizeGlyph()
    {
        bool maximized = WindowState == WindowState.Maximized;
        MaxGlyph.Text = maximized ? "\uE923" : "\uE922";
        MaxButton.ToolTip = maximized ? "还原" : "最大化";
    }

    private void OpenSourceEditor(SourceProfile draft, bool isNew)
    {
        SourceEditDialog dialog = new(draft, isNew) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _viewModel.CommitSourceEdit(dialog.Draft, isNew);
        }
    }

    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFileDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        string[]? files = e.Data.GetData(DataFormats.FileDrop) as string[];
        string? image = files?.FirstOrDefault(path => ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()));

        if (image is not null)
        {
            _ = _viewModel.ImportDroppedAsync(image);
        }
    }

    public void DisposeViewModel() => _viewModel.Shutdown();
}
