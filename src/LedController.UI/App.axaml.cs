using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using LedController.Core.Interfaces;
using LedController.Infrastructure.Services;
using LedController.UI.ViewModels;
using LedController.UI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LedController.UI;

public partial class App : Application
{
    private SplashScreenWindow? _splashScreen;

    public IServiceProvider Services { get; private set; } = default!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        RegisterGlobalExceptionHandlers();
        NormalizeStartupRegistration();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (Program.ShowSplashOnStartup)
            {
                _splashScreen = new SplashScreenWindow();
                _splashScreen.Show();
            }

            var mainWindow = Services.GetRequiredService<MainWindow>();
            mainWindow.ConfigureStartup(Program.StartMinimizedToTray);
            mainWindow.Opened += (_, _) => CloseSplash();
            mainWindow.Closed += (_, _) => CloseSplash();
            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void NormalizeStartupRegistration()
    {
        try
        {
            var startupService = Services.GetService<IStartupService>();
#if !WINDOWS
            if (startupService is LinuxStartupService linuxStartupService)
            {
                linuxStartupService.NormalizeExistingRegistration();
                if (File.Exists(linuxStartupService.UserServicePath))
                {
                    return;
                }
            }
#endif

            if (startupService?.IsEnabled() == true)
            {
                _ = startupService.SetEnabled(enabled: true);
            }
        }
        catch
        {
        }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IConfigService, FileConfigService>();
        services.AddSingleton<ILocationService, LocationService>();
        services.AddSingleton<ISchedulerService, SchedulerService>();
        services.AddSingleton<IMqttService, MqttService>();

#if WINDOWS
        services.AddSingleton<IBleService, WindowsBleService>();
        services.AddSingleton<IStartupService, WindowsStartupService>();
#else
        services.AddSingleton<IBleService, LinuxBleService>();
        services.AddSingleton<IStartupService, LinuxStartupService>();
#endif

        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<DiscoveryViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
    }

    private static void RegisterGlobalExceptionHandlers()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            AppLog.Exception("Unhandled UI thread exception.", e.Exception);
            e.Handled = true;
        };
    }

    private void CloseSplash()
    {
        if (_splashScreen is null)
        {
            return;
        }

        try
        {
            _splashScreen.Close();
        }
        catch
        {
        }
        finally
        {
            _splashScreen = null;
        }
    }
}
