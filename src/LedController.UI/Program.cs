using Avalonia;
using System;
using System.Linq;
using System.Threading.Tasks;
using LedController.UI.Services;

namespace LedController.UI;

class Program
{
    private const string StartMinimizedArgument = "--start-minimized-to-tray";

    internal static bool StartMinimizedToTray { get; private set; }

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        StartMinimizedToTray = args.Any(a => string.Equals(a, StartMinimizedArgument, StringComparison.OrdinalIgnoreCase));

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var exception = e.ExceptionObject as Exception ?? new Exception("Unknown unhandled exception.");
            AppLog.Exception("Unhandled exception (AppDomain).", exception);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Exception("Unobserved task exception.", e.Exception);
            e.SetObserved();
        };

        if (!StartMinimizedToTray)
        {
            NativeSplash.Show();
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
