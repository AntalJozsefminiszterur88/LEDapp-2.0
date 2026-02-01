using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform;
using LedController.UI.ViewModels;
using LedController.UI.Views;

namespace LedController.UI;

public partial class MainWindow : Window
{
    private TrayIcon? _trayIcon;
    private bool _exitRequested;

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
        Closed += (_, _) => DisposeTrayIcon();
        Opened += (_, _) => EnsureTrayIcon();
    }

    public MainWindow(MainViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
        viewModel.DiscoveryRequested += async () => await OpenDiscoveryAsync(viewModel);
    }

    private async Task OpenDiscoveryAsync(MainViewModel viewModel)
    {
        var dialog = new Window
        {
            Title = "Eszközök keresése",
            Width = 700,
            Height = 500,
            Icon = Icon,
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

        var icon = new WindowIcon(AssetLoader.Open(new Uri("avares://LedController.UI/Assets/logo.ico")));
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
}
