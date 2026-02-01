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
    private string macAddress = string.Empty;

    [ObservableProperty]
    private bool isConnected;

    [ObservableProperty]
    private LedColor? currentColor;

    [ObservableProperty]
    private int brightness = 100;

    [ObservableProperty]
    private bool isOn;

    [ObservableProperty]
    private ObservableCollection<ScheduleProfile> profiles = new();
}
