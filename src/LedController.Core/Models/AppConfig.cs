namespace LedController.Core.Models;

public sealed record AppConfig(
    IReadOnlyList<LedDevice> SavedDevices,
    IReadOnlyList<ScheduleProfile> Profiles,
    AppSettings Settings)
{
    public static AppConfig Empty { get; } =
        new(Array.Empty<LedDevice>(), Array.Empty<ScheduleProfile>(), AppSettings.Default);
}
