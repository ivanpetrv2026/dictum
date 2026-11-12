using System.Threading;
using System.Windows;
using Dictum.Services;
using Dictum.Settings;
using Dictum.Views;

namespace Dictum;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private AppController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--setup"))
        {
            _controller = new AppController();
            _controller.Start();
            _controller.ShowOnboarding();
            return;
        }

        if (e.Args.Contains("--indicator-test"))
        {
            var indicator = new RecordingIndicator();
            indicator.PlaceNearBottom();
            indicator.Show();

            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                indicator.Close();
                Shutdown();
            };
            timer.Start();
            return;
        }

        if (e.Args.Contains("--ai-test"))
        {
            var output = new System.Text.StringBuilder();
            try
            {
                var index = Array.IndexOf(e.Args, "--ai-test");
                var sample = index + 1 < e.Args.Length
                    ? e.Args[index + 1]
                    : "привет это тест пунктуации и очистки текста мы используем click house и github";

                var settings = new SettingsStore().Load();
                var cleanup = new AiCleanup(
                    settings.AiProvider,
                    AiCleanup.ResolveKey(settings.AiProvider) ?? string.Empty,
                    settings.AiModel,
                    settings.AiBaseUrl);

                output.AppendLine("provider: " + settings.AiProvider);
                output.AppendLine("endpoint: " + settings.AiBaseUrl);
                output.AppendLine("model: " + settings.AiModel);
                output.AppendLine("configured: " + cleanup.IsConfigured);
                output.AppendLine("in : " + sample);

                var result = cleanup.CleanAsync(sample, 20).GetAwaiter().GetResult();
                output.AppendLine("out: " + (result ?? "<null>"));

                if (result is null && settings.AiFallbackEnabled)
                {
                    var fallbackCleanup = new AiCleanup(
                        "gemini",
                        AiCleanup.ResolveKey("gemini") ?? string.Empty,
                        settings.AiFallbackModel,
                        AiCleanup.DefaultEndpoint("gemini"));

                    output.AppendLine("fallback configured: " + fallbackCleanup.IsConfigured);
                    var fallbackResult = fallbackCleanup.CleanAsync(sample, 20).GetAwaiter().GetResult();
                    output.AppendLine("fallback out: " + (fallbackResult ?? "<null>"));
                }
            }
            catch (Exception ex)
            {
                output.AppendLine("error: " + ex);
            }

            var aiPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Dictum",
                "ai-test.txt");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(aiPath)!);
            System.IO.File.WriteAllText(aiPath, output.ToString());
            Shutdown();
            return;
        }

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

        if (e.Args.Contains("--settings"))
        {
            _controller.ShowSettings();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
