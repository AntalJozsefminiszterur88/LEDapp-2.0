using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using LedController.Core.Interfaces;
using LedController.Core.Models;

namespace LedController.UI.ViewModels;

public sealed partial class DashboardViewModel : ViewModelBase
{
    private readonly IConfigService _configService;

    public DashboardViewModel(IConfigService configService)
    {
        _configService = configService;
        SavedDevices = new ObservableCollection<LedDevice>();
        _ = LoadAsync();
    }

    public ObservableCollection<LedDevice> SavedDevices { get; }

    public event Action<LedDevice>? DeviceControlRequested;

    [RelayCommand]
    private void GoToDeviceControl(LedDevice? device)
    {
        if (device is null)
        {
            return;
        }

        DeviceControlRequested?.Invoke(device);
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
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Dashboard] Failed to load devices: {ex.Message}");
        }
    }
}
