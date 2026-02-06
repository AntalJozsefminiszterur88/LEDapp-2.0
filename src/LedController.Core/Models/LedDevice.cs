using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LedController.Core.Models;

public partial class LedDevice : ObservableObject
{
    [ObservableProperty]
    private Guid id = Guid.NewGuid();

    [ObservableProperty]
    private string name = string.Empty;

    [ObservableProperty]
    private string customName = string.Empty;

    [ObservableProperty]
    private string macAddress = string.Empty;

    [ObservableProperty]
    private bool isConnected;

    [ObservableProperty]
    private bool isConnecting;

    [ObservableProperty]
    private LedColor? currentColor;

    [ObservableProperty]
    private int brightness = 100;

    [ObservableProperty]
    private bool isOn;

    [ObservableProperty]
    private ObservableCollection<ScheduleProfile> profiles = new();

    public string PrimaryName => string.IsNullOrWhiteSpace(CustomName) ? Name : CustomName;

    public string SecondaryName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(CustomName))
            {
                return MacAddress;
            }

            if (string.IsNullOrWhiteSpace(Name))
            {
                return MacAddress;
            }

            if (string.IsNullOrWhiteSpace(MacAddress))
            {
                return Name;
            }

            return $"{Name} - {MacAddress}";
        }
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
