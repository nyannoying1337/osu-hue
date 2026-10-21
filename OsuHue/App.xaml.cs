using System.IO;
using System.Windows;

namespace OsuHue;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool screenshot = e.Args.Contains("--screenshot");
        bool lifecycleTest = e.Args.Contains("--lifecycle-test");
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            if (screenshot || lifecycleTest)
            {
                File.WriteAllText(System.IO.Path.Combine(Environment.CurrentDirectory, "work/OsuHue-test-error.txt"), args.Exception.ToString());
                Shutdown(1);
            }
            else
            {
                AppLog.Error("Unhandled UI error", args.Exception);
                MessageBox.Show(UiText.Translate("Die App wird nach einem unerwarteten Fehler geschlossen: " + args.Exception.Message), "osu!hue", MessageBoxButton.OK, MessageBoxImage.Error);
                MainWindow?.Close();
            }
        };
        var window = new MainWindow(screenshot, lifecycleTest);
        MainWindow = window;
        if (screenshot)
        {
            window.CapturePreview(e.Args.Contains("--compact"));
            Shutdown();
        }
        else if (lifecycleTest)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            window.Closed += (_, _) =>
            {
                File.WriteAllText(System.IO.Path.Combine(Environment.CurrentDirectory, "work/OsuHue-lifecycle-ok.txt"), "Normal window close completed without network, settings writes or error dialogs.");
                Shutdown();
            };
            Dispatcher.BeginInvoke(() => window.Close());
        }
        else
            window.Show();
    }
}
