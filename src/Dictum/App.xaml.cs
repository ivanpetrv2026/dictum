using System.Threading;
using System.Windows;
using Dictum.Services;

namespace Dictum;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private AppController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, @"Local\Dictum.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        _controller = new AppController();
        _controller.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
