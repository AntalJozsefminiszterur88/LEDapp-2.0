using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LedController.Core.Models;

public partial class LedDevice : ObservableObject
{
    [ObservableProperty]
    private Guid id = Guid.NewGuid();

    [ObservableProperty]
    private string deviceIdentifier = string.Empty;

    [ObservableProperty]
    private string name = string.Empty;

    [ObservableProperty]
    private string customName = string.Empty;

    [ObservableProperty]
    private string macAddress = string.Empty;

    [ObservableProperty]
    [property: System.Text.Json.Serialization.JsonIgnore]
    private bool isConnected;

    [ObservableProperty]
    [property: System.Text.Json.Serialization.JsonIgnore]
    private bool isConnecting;

    [ObservableProperty]
    private LedColor? currentColor;

    [ObservableProperty]
    private int brightness = 100;

    [ObservableProperty]
    private bool isOn;

    [ObservableProperty]
    private bool scheduleEnabled = true;

    [ObservableProperty]
    private ObservableCollection<ScheduleProfile> profiles = new();

    public string PrimaryName => string.IsNullOrWhiteSpace(CustomName) ? Name : CustomName;

    public string SecondaryName
    {
        get
        {
            var addressOrIdentifier = string.IsNullOrWhiteSpace(MacAddress) ? DeviceIdentifier : MacAddress;

            if (string.IsNullOrWhiteSpace(CustomName))
            {
                return addressOrIdentifier;
            }

            if (string.IsNullOrWhiteSpace(Name))
            {
                return addressOrIdentifier;
            }

            if (string.IsNullOrWhiteSpace(addressOrIdentifier))
            {
                return Name;
            }

            return $"{Name} - {addressOrIdentifier}";
        }
    }

    partial void OnDeviceIdentifierChanged(string value)
    {
        OnPropertyChanged(nameof(SecondaryName));
    }

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(PrimaryName));
        OnPropertyChanged(nameof(SecondaryName));
    }

    partial void OnCustomNameChanged(string value)
    {
        OnPropertyChanged(nameof(PrimaryName));
        OnPropertyChanged(nameof(SecondaryName));
    }

    partial void OnMacAddressChanged(string value)
    {
        OnPropertyChanged(nameof(SecondaryName));
    }
}
