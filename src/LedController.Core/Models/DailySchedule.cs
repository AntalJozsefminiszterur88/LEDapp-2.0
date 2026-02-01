using CommunityToolkit.Mvvm.ComponentModel;

namespace LedController.Core.Models;

public partial class DailySchedule : ObservableObject
{
    [ObservableProperty]
    private DayOfWeek dayOfWeek;

    [ObservableProperty]
    private bool sunriseEnabled;

    [ObservableProperty]
    private int sunriseOffset;

    [ObservableProperty]
    private bool sunsetEnabled;

    [ObservableProperty]
    private int sunsetOffset;

    [ObservableProperty]
    private TimeSpan? fixedOnTime;

    [ObservableProperty]
    private TimeSpan? fixedOffTime;

    [ObservableProperty]
    private LedColor? targetColor;
}
