using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LedController.Core.Models;

public partial class ScheduleProfile : ObservableObject
{
    [ObservableProperty]
    private string name = string.Empty;

    [ObservableProperty]
    private bool isActive = true;

    [ObservableProperty]
    private ObservableCollection<DailySchedule> dailySchedules = new();

    public DailySchedule? GetSchedule(DayOfWeek dayOfWeek)
    {
        return DailySchedules.FirstOrDefault(x => x.DayOfWeek == dayOfWeek);
    }
}
