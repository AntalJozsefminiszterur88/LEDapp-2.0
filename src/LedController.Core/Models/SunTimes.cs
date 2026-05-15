namespace LedController.Core.Models;

public sealed record SunTimes(DateTime? Sunrise, DateTime? Sunset)
{
    public static SunTimes Empty { get; } = new(null, null);
}
