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

        if (e.Args.Contains("--selftest"))
        {
            try
            {
                SelfTest.Run();
            }
            catch (Exception ex)
            {
                var errorPath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Dictum",
                    "selftest-error.txt");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(errorPath)!);
                System.IO.File.WriteAllText(errorPath, ex.ToString());
            }

            Shutdown();
            return;
        }

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
