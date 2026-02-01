using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LedController.Core.Interfaces;
using LedController.Core.Models;

namespace LedController.UI.ViewModels;

public sealed partial class DiscoveryViewModel : ViewModelBase
{
    private readonly IBleService _bleService;
    private readonly IConfigService _configService;

    public DiscoveryViewModel(IBleService bleService, IConfigService configService)
    {
        _bleService = bleService;
        _configService = configService;
        DiscoveredDevices = new ObservableCollection<LedDevice>();
    }

    public ObservableCollection<LedDevice> DiscoveredDevices { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private LedDevice? selectedDevice;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private bool isBusy;

    private bool CanScan() => !IsBusy;

    private bool CanConnect() => !IsBusy && SelectedDevice is not null;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            var devices = await _bleService.ScanForDevicesAsync();

            DiscoveredDevices.Clear();
            foreach (var device in devices)
            {
                DiscoveredDevices.Add(device);
            }

            SelectedDevice = DiscoveredDevices.Count > 0 ? DiscoveredDevices[0] : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Discovery] Scan failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        if (IsBusy || SelectedDevice is null)
        {
            return;
        }

        try
        {
            IsBusy = true;
            await _bleService.ConnectAsync(SelectedDevice);

            var config = await _configService.LoadConfigAsync();
            var updatedDevices = config.SavedDevices.ToList();

            if (!updatedDevices.Any(d => string.Equals(d.MacAddress, SelectedDevice.MacAddress, StringComparison.OrdinalIgnoreCase)))
            {
                updatedDevices.Add(SelectedDevice);
            }

            var updatedConfig = new AppConfig(updatedDevices, config.Profiles, config.Settings);
            await _configService.SaveConfigAsync(updatedConfig);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Discovery] Connect failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }
}
