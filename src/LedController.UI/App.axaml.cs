using System;
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

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = Services.GetRequiredService<MainWindow>();
            mainWindow.Opened += (_, _) => NativeSplash.Close();
            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IBleService, BleService>();
        services.AddSingleton<IConfigService, FileConfigService>();
        services.AddSingleton<ILocationService, LocationService>();
        services.AddSingleton<ISchedulerService, SchedulerService>();
        services.AddSingleton<IMqttService, MqttService>();

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
}
