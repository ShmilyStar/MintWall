using System.Windows;
using MintWall.Services;

namespace MintWall;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DataPaths.EnsureCreated();

        if (e.Args.Length > 0)
        {
            // Run on the thread pool: awaiting on the dispatcher thread would deadlock the HTTP continuations.
            int code = Task.Run(() => CliRunner.RunAsync(e.Args)).GetAwaiter().GetResult();
            Shutdown(code);
            return;
        }

        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        (MainWindow as MainWindow)?.DisposeViewModel();
        base.OnExit(e);
    }
}
