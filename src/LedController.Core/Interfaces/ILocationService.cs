using LedController.Core.Models;

namespace LedController.Core.Interfaces;

public interface ILocationService
{
    Task<GeoCoordinate> GetCurrentLocationAsync();
    SunTimes GetSunTimes(double lat, double lon, DateTime date);
}
