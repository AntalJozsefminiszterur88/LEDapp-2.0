using LedController.Core.Interfaces;
using LedController.Core.Models;

namespace LedController.Infrastructure.Services;

public sealed class PlaceholderLocationService : ILocationService
{
    public Task<GeoCoordinate> GetCurrentLocationAsync()
    {
        var settings = AppSettings.Default;
        return Task.FromResult(new GeoCoordinate(settings.Latitude, settings.Longitude));
    }

    public SunTimes GetSunTimes(double lat, double lon, DateTime date)
    {
        return SunTimes.Empty;
    }
}
