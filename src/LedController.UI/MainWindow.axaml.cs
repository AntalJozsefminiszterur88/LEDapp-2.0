using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Platform;
using Avalonia.Media;
using Avalonia.Interactivity;
using Avalonia.Threading;
using LedController.UI.ViewModels;
using LedController.UI.Views;

namespace LedController.UI;

public partial class MainWindow : Window
{
    private TrayIcon? _trayIcon;
    private bool _exitRequested;
    private bool _sunTimesRefreshed;
    private bool _startHiddenInTray;

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
        Closed += (_, _) => DisposeTrayIcon();
        Opened += (_, _) => EnsureTrayIcon();
        Opened += (_, _) => ApplyStartupVisibility();
    }

    public MainWindow(MainViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
        viewModel.DiscoveryRequested += async () => await OpenDiscoveryAsync(viewModel);
        Opened += (_, _) => _ = InitializeAfterOpenAsync(viewModel);
    }

    internal void ConfigureStartup(bool startHiddenInTray)
    {
        _startHiddenInTray = startHiddenInTray;
    }

    private async Task OpenDiscoveryAsync(MainViewModel viewModel)
    {
        var dialog = new Window
        {
            Title = "Eszközök keresése",
            Width = 700,
            Height = 500,
            Icon = Icon,
            Background = new SolidColorBrush(Color.Parse("#1E1E1E")),
            Content = new DiscoveryView
            {
                DataContext = viewModel.Discovery
            },
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        await dialog.ShowDialog(this);
        await viewModel.RefreshDevicesAsync();
    }

    private void EnsureTrayIcon()
    {
        if (_trayIcon is not null)
        {
            return;
        }

        var icon = CreateTrayIcon();
        var menu = new NativeMenu();

        var showHideItem = new NativeMenuItem("Megjelenítés / elrejtés");
        showHideItem.Click += (_, _) => ToggleVisibility();

        var exitItem = new NativeMenuItem("Kilépés");
        exitItem.Click += (_, _) => ExitApplication();

        menu.Items.Add(showHideItem);
        menu.Items.Add(exitItem);

        _trayIcon = new TrayIcon
        {
            Icon = icon,
            ToolTipText = "LedController",
            Menu = menu
        };
        _trayIcon.Clicked += (_, _) => ShowFromTray();
    }

    private static WindowIcon CreateTrayIcon()
    {
#if WINDOWS
        const string assetPath = "avares://LEDapp-2.0/Assets/logo.ico";
#else
        const string assetPath = "avares://LEDapp-2.0/Assets/logo.png";
#endif

        return new WindowIcon(AssetLoader.Open(new Uri(assetPath)));
    }

    private void ToggleVisibility()
    {
        if (IsVisible)
        {
            Hide();
        }
        else
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }
    }

    private void ShowFromTray()
    {
        if (!IsVisible)
        {
            Show();
        }

        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _exitRequested = true;
        Close();
    }

    private void ApplyStartupVisibility()
    {
        if (!_startHiddenInTray)
        {
            return;
        }

        _startHiddenInTray = false;
        Dispatcher.UIThread.Post(Hide, DispatcherPriority.Background);
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_exitRequested)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }

    private void DisposeTrayIcon()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
    }

    private void OnCustomNameSaveClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control control)
        {
            return;
        }

        var flyout = control.GetLogicalAncestors().OfType<Flyout>().FirstOrDefault();
        flyout?.Hide();
    }

    private void OnRemoveDeviceClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control control)
        {
            return;
        }

        var flyout = control.GetLogicalAncestors().OfType<Flyout>().FirstOrDefault();
        flyout?.Hide();
    }

    private async Task RefreshSunTimesAfterOpenAsync(MainViewModel viewModel)
    {
        if (_sunTimesRefreshed)
        {
            return;
        }

        _sunTimesRefreshed = true;
        await viewModel.RefreshSunTimesAsync();
    }

    private async Task InitializeAfterOpenAsync(MainViewModel viewModel)
    {
        await Task.Yield();
        await viewModel.InitializeAfterOpenAsync();
        await RefreshSunTimesAfterOpenAsync(viewModel);
    }
}
