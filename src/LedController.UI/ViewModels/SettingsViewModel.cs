using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LedController.Core.Interfaces;
using LedController.Core.Models;

namespace LedController.UI.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly IConfigService _configService;
    private readonly IMqttService _mqttService;

    public SettingsViewModel(IConfigService configService, IMqttService mqttService)
    {
        _configService = configService;
        _mqttService = mqttService;
        _ = LoadAsync();
    }

    [ObservableProperty]
    private bool startWithWindows;

    [ObservableProperty]
    private bool mqttEnabled;

    [ObservableProperty]
    private string mqttHost = string.Empty;

    [ObservableProperty]
    private int mqttPort = 1883;

    [ObservableProperty]
    private string mqttUsername = string.Empty;

    [ObservableProperty]
    private string mqttPassword = string.Empty;

    [ObservableProperty]
    private bool isMqttRunning;

    public string MqttToggleLabel => IsMqttRunning ? "MQTT leállítás" : "MQTT indítás";

    partial void OnIsMqttRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(MqttToggleLabel));
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            var mqttSettings = new MqttSettings
            {
                Enabled = MqttEnabled,
                Host = MqttHost ?? string.Empty,
                Port = MqttPort <= 0 ? 1883 : MqttPort,
                Username = string.IsNullOrWhiteSpace(MqttUsername) ? null : MqttUsername,
                Password = string.IsNullOrWhiteSpace(MqttPassword) ? null : MqttPassword
            };

            var settings = (config.Settings ?? AppSettings.Default) with
            {
                StartWithWindows = StartWithWindows,
                Mqtt = mqttSettings
            };

            var updated = new AppConfig(config.SavedDevices, config.Profiles, settings);
            await _configService.SaveConfigAsync(updated);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Settings] Save failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ToggleMqttAsync()
    {
        try
        {
            if (IsMqttRunning)
            {
                await _mqttService.StopAsync();
            }
            else
            {
                await SaveAsync();
                await _mqttService.StartAsync();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Settings] MQTT toggle failed: {ex.Message}");
        }
        finally
        {
            IsMqttRunning = _mqttService.IsRunning;
        }
    }

    private async Task LoadAsync()
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            var settings = config.Settings ?? AppSettings.Default;
            var mqtt = settings.Mqtt ?? MqttSettings.Default;

            StartWithWindows = settings.StartWithWindows;
            MqttEnabled = mqtt.Enabled;
            MqttHost = mqtt.Host;
            MqttPort = mqtt.Port == 0 ? 1883 : mqtt.Port;
            MqttUsername = mqtt.Username ?? string.Empty;
            MqttPassword = mqtt.Password ?? string.Empty;
            IsMqttRunning = _mqttService.IsRunning;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Settings] Load failed: {ex.Message}");
        }
    }
}
