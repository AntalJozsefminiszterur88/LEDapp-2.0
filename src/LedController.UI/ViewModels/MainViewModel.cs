using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LedController.Core.Interfaces;
using LedController.Core.Models;

namespace LedController.UI.ViewModels;

public sealed partial class MainViewModel : ViewModelBase
{
    private readonly IBleService _bleService;
    private readonly IConfigService _configService;
    private readonly ILocationService _locationService;
    private readonly IMqttService _mqttService;
    private readonly DispatcherTimer _clockTimer;

    [ObservableProperty]
    private LedDevice? selectedDevice;

    public MainViewModel(
        ISchedulerService schedulerService,
        IBleService bleService,
        IConfigService configService,
        ILocationService locationService,
        IMqttService mqttService,
        DiscoveryViewModel discoveryViewModel,
        SettingsViewModel settingsViewModel)
    {
        _bleService = bleService;
        _configService = configService;
        _locationService = locationService;
        _mqttService = mqttService;
        Discovery = discoveryViewModel;
        Settings = settingsViewModel;

        schedulerService.Start();
        _ = StartMqttIfEnabledAsync();

        SavedDevices = new ObservableCollection<LedDevice>();
        _ = LoadAsync();

        _clockTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _clockTimer.Tick += (_, _) => UpdateTime();
        _clockTimer.Start();
        UpdateTime();
    }

    public ObservableCollection<LedDevice> SavedDevices { get; }

    public DiscoveryViewModel Discovery { get; }

    public SettingsViewModel Settings { get; }

    public event Action? DiscoveryRequested;

    [ObservableProperty]
    private DeviceControlViewModel? deviceControl;

    [ObservableProperty]
    private SchedulerViewModel? scheduler;

    [ObservableProperty]
    private string currentTimeText = "--:--";

    [ObservableProperty]
    private string sunriseText = "--:--";

    [ObservableProperty]
    private string sunsetText = "--:--";

    partial void OnSelectedDeviceChanged(LedDevice? value)
    {
        if (value is null)
        {
            DeviceControl = null;
            Scheduler = null;
            return;
        }

        DeviceControl = new DeviceControlViewModel(_bleService, value);
        Scheduler = new SchedulerViewModel(_configService, value);
    }

    [RelayCommand]
    private void OpenDiscovery()
    {
        DiscoveryRequested?.Invoke();
    }

    public async Task RefreshDevicesAsync()
    {
        await LoadAsync();
    }

    private async Task StartMqttIfEnabledAsync()
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            if (config.Settings?.Mqtt?.Enabled == true)
            {
                await _mqttService.StartAsync();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Main] MQTT start failed: {ex.Message}");
        }
    }

    private async Task LoadAsync()
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            SavedDevices.Clear();
            foreach (var device in config.SavedDevices)
            {
                SavedDevices.Add(device);
            }

            SelectedDevice = SavedDevices.FirstOrDefault();

            await UpdateSunTimesAsync(config);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Main] Load failed: {ex.Message}");
        }
    }

    private async Task UpdateSunTimesAsync(AppConfig config)
    {
        try
        {
            var settings = config.Settings ?? AppSettings.Default;
            var coords = new GeoCoordinate(settings.Latitude, settings.Longitude);
            if (coords.Latitude == 0 && coords.Longitude == 0)
            {
                coords = await _locationService.GetCurrentLocationAsync();
            }

            var sunTimes = _locationService.GetSunTimes(coords.Latitude, coords.Longitude, DateTime.Now);
            SunriseText = sunTimes.Sunrise?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "--:--";
            SunsetText = sunTimes.Sunset?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "--:--";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Main] Sun time update failed: {ex.Message}");
            SunriseText = "--:--";
            SunsetText = "--:--";
        }
    }

    private void UpdateTime()
    {
        CurrentTimeText = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
    }
}
