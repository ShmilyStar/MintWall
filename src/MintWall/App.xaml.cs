using System.Threading;
using System.Windows;
using MintWall.Native;
using MintWall.Services;

namespace MintWall;

public partial class App : Application
{
    private const string MutexName = @"Local\MintWall.Desktop.SingleInstance";
    private const string ShowEventName = @"Local\MintWall.Desktop.ShowMainWindow";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;

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

        // 事件先于 mutex 创建：第二个进程 Set 时即使监听线程还没起来，信号也会保留到被 WaitOne 取走。
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _mutex = new Mutex(false, MutexName);
        bool first;
        try
        {
            first = _mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // 上个进程被强杀留下 abandoned mutex：WaitOne 抛异常的同时把所有权交给本次进程。
            first = true;
        }

        if (!first)
        {
            _showEvent.Set();
            _showEvent.Dispose();
            _showEvent = null;
            _mutex.Dispose();
            _mutex = null;
            Shutdown(0);
            return;
        }

        MainWindow = new MainWindow();
        MainWindow.Show();

        var listener = new Thread(() =>
        {
            while (_showEvent!.WaitOne())
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (MainWindow is not MainWindow window)
                    {
                        return;
                    }

                    window.Show();
                    if (window.WindowState == WindowState.Minimized)
                    {
                        window.WindowState = WindowState.Normal;
                    }

                    window.Activate();
                });
            }
        })
        {
            IsBackground = true,
            Name = "MintWall.Activation"
        };
        listener.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        (MainWindow as MainWindow)?.DisposeViewModel();
        DesktopWallpaperFactory.Release();
        // 不 dispose 事件：监听线程还阻塞在 WaitOne 上，dispose 会让它抛未处理异常；句柄随进程退出回收。
        if (_mutex is not null)
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }

        base.OnExit(e);
    }
}
