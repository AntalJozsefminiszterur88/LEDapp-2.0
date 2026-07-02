using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
    private readonly ISchedulerService _schedulerService;
    private readonly IBleService _bleService;
    private readonly IConfigService _configService;
    private readonly ILocationService _locationService;
    private readonly IMqttService _mqttService;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _healthTimer;
    private LedDevice? _pendingSchedulerDevice;
    private bool _suppressUiStateSave;
    private bool _initializedAfterOpen;
    private bool _healthCheckRunning;
    private readonly HashSet<LedDevice> _deviceSubscriptions = new();

    [ObservableProperty]
    private LedDevice? selectedDevice;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedDeviceCommand))]
    private LedDevice? deviceToRemove;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedDeviceCommand))]
    private bool isRemovingDevice;

    public MainViewModel(
        ISchedulerService schedulerService,
        IBleService bleService,
        IConfigService configService,
        ILocationService locationService,
        IMqttService mqttService,
        DiscoveryViewModel discoveryViewModel,
        SettingsViewModel settingsViewModel)
    {
        _schedulerService = schedulerService;
        _bleService = bleService;
        _configService = configService;
        _locationService = locationService;
        _mqttService = mqttService;
        _settingsViewModel = settingsViewModel;
        Discovery = discoveryViewModel;
        Settings = null;

        SavedDevices = new ObservableCollection<LedDevice>();

        _clockTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _clockTimer.Tick += (_, _) => UpdateTime();
        _clockTimer.Start();
        UpdateTime();

        _healthTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _healthTimer.Tick += async (_, _) => await RunHealthCheckAsync();
    }

    public ObservableCollection<LedDevice> SavedDevices { get; }

    public DiscoveryViewModel Discovery { get; }

    [ObservableProperty]
    private SettingsViewModel? settings;

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

    [ObservableProperty]
    private bool isSchedulerExpanded;

    [ObservableProperty]
    private bool isSettingsExpanded;

    partial void OnSelectedDeviceChanged(LedDevice? value)
    {
        if (DeviceControl is IDisposable disposableDeviceControl)
        {
            disposableDeviceControl.Dispose();
        }

        if (Scheduler is IDisposable disposableScheduler)
        {
            disposableScheduler.Dispose();
        }

        if (value is null)
        {
            DeviceControl = null;
            Scheduler = null;
            _pendingSchedulerDevice = null;
            return;
        }

        DeviceControl = new DeviceControlViewModel(_bleService, _mqttService, _schedulerService, _configService, value);
        _pendingSchedulerDevice = value;
        EnsureSchedulerLoaded();
    }

    [RelayCommand]
    private void OpenDiscovery()
    {
        DiscoveryRequested?.Invoke();
    }

    private bool CanRemoveSelectedDevice() => !IsRemovingDevice && DeviceToRemove is not null;

    [RelayCommand(CanExecute = nameof(CanRemoveSelectedDevice))]
    private async Task RemoveSelectedDeviceAsync()
    {
        var target = DeviceToRemove;
        if (target is null || IsRemovingDevice)
        {
            return;
        }

        IsRemovingDevice = true;
        try
        {
            if (target.IsConnected || target.IsConnecting)
            {
                try
                {
                    await _bleService.DisconnectAsync(target);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Main] Device disconnect before removal failed: {ex.Message}");
                }
            }

            var config = await _configService.LoadConfigAsync();
            var updatedDevices = config.SavedDevices
                .Where(device => !IsSameDevice(device, target))
                .ToList();

            var updatedConfig = new AppConfig(updatedDevices, config.Profiles, config.Settings);
            await _configService.SaveConfigAsync(updatedConfig);

            var toRemove = SavedDevices
                .Where(device => IsSameDevice(device, target))
                .ToList();

            foreach (var device in toRemove)
            {
                SavedDevices.Remove(device);
                DetachDevice(device);
            }

            if (SavedDevices.Count == 0)
            {
                SelectedDevice = null;
                DeviceToRemove = null;
            }
            else
            {
                if (SelectedDevice is null || IsSameDevice(SelectedDevice, target))
                {
                    SelectedDevice = SavedDevices.FirstOrDefault();
                }

                if (DeviceToRemove is null || IsSameDevice(DeviceToRemove, target))
                {
                    DeviceToRemove = SavedDevices.FirstOrDefault();
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Main] Device removal failed: {ex.Message}");
        }
        finally
        {
            IsRemovingDevice = false;
        }
    }

    public async Task RefreshDevicesAsync()
    {
        await LoadAsync();
    }

    public async Task InitializeAfterOpenAsync()
    {
        if (_initializedAfterOpen)
        {
            return;
        }

        _initializedAfterOpen = true;
        _schedulerService.Start();
        _ = StartMqttIfEnabledAsync();
        await LoadAsync();
        _healthTimer.Start();
    }

    public async Task RefreshSunTimesAsync()
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            await UpdateSunTimesAsync(config);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Main] Sun time refresh failed: {ex.Message}");
            SunriseText = "--:--";
            SunsetText = "--:--";
        }
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
            ResetDeviceSubscriptions();
            SavedDevices.Clear();
            foreach (var device in config.SavedDevices)
            {
                SavedDevices.Add(device);
                AttachDevice(device);
            }

            SelectedDevice = SavedDevices.FirstOrDefault();
            DeviceToRemove = SavedDevices.FirstOrDefault();

            var settings = config.Settings ?? AppSettings.Default;
            _suppressUiStateSave = true;
            IsSchedulerExpanded = settings.SchedulerExpanded;
            IsSettingsExpanded = settings.SettingsExpanded;
            _suppressUiStateSave = false;

        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Main] Load failed: {ex.Message}");
        }
    }

    partial void OnIsSchedulerExpandedChanged(bool value)
    {
        if (value)
        {
            EnsureSchedulerLoaded();
        }

        _ = SaveUiStateAsync();
    }

    partial void OnIsSettingsExpandedChanged(bool value)
    {
        if (value && Settings is null)
        {
            Settings = _settingsViewModel;
        }

        _ = SaveUiStateAsync();
    }

    private async Task SaveUiStateAsync()
    {
        if (_suppressUiStateSave)
        {
            return;
        }

        try
        {
            var config = await _configService.LoadConfigAsync();
            var settings = config.Settings ?? AppSettings.Default;
            settings = settings with
            {
                SchedulerExpanded = IsSchedulerExpanded,
                SettingsExpanded = IsSettingsExpanded
            };

            var updated = config with { Settings = settings };
            await _configService.SaveConfigAsync(updated);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Main] UI state save failed: {ex.Message}");
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

    private void EnsureSchedulerLoaded()
    {
        if (!IsSchedulerExpanded || _pendingSchedulerDevice is null)
        {
            return;
        }

        if (Scheduler?.TargetDevice.Id == _pendingSchedulerDevice.Id)
        {
            return;
        }

        if (Scheduler is IDisposable disposableScheduler)
        {
            disposableScheduler.Dispose();
        }

        Scheduler = new SchedulerViewModel(_configService, _schedulerService, _locationService, _pendingSchedulerDevice);
    }

    private async Task RunHealthCheckAsync()
    {
        if (_healthCheckRunning)
        {
            return;
        }

        _healthCheckRunning = true;
        try
        {
            _schedulerService.Start();

            if (!_mqttService.IsRunning)
            {
                await StartMqttIfEnabledAsync();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Main] Health check failed: {ex.Message}");
        }
        finally
        {
            _healthCheckRunning = false;
        }
    }

    private void AttachDevice(LedDevice device)
    {
        if (_deviceSubscriptions.Add(device))
        {
            device.PropertyChanged += OnDevicePropertyChanged;
        }
    }

    private void DetachDevice(LedDevice device)
    {
        if (_deviceSubscriptions.Remove(device))
        {
            device.PropertyChanged -= OnDevicePropertyChanged;
        }
    }

    private void ResetDeviceSubscriptions()
    {
        foreach (var device in _deviceSubscriptions)
        {
            device.PropertyChanged -= OnDevicePropertyChanged;
        }

        _deviceSubscriptions.Clear();
    }

    private static bool IsSameDevice(LedDevice left, LedDevice right)
    {
        return LedDeviceIdentity.Matches(left, right);
    }

    private void OnDevicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not LedDevice device)
        {
            return;
        }

        if (e.PropertyName == nameof(LedDevice.CustomName))
        {
            _ = SaveDeviceCustomNameAsync(device);
        }
    }

    private async Task SaveDeviceCustomNameAsync(LedDevice device)
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            var devices = config.SavedDevices.ToList();
            var target = devices.FirstOrDefault(d => LedDeviceIdentity.Matches(d, device));

            if (target is null)
            {
                target = device;
                devices.Add(target);
            }

            target.CustomName = device.CustomName ?? string.Empty;

            var updated = new AppConfig(devices, config.Profiles, config.Settings);
            await _configService.SaveConfigAsync(updated);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Main] Device name save failed: {ex.Message}");
        }
    }

}
