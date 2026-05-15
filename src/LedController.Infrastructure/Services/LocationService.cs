using System.Net.Http;
using System.Text.Json;
using LedController.Core.Interfaces;
using LedController.Core.Models;

namespace LedController.Infrastructure.Services;

public sealed class LocationService : ILocationService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    public async Task<GeoCoordinate> GetCurrentLocationAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "http://ip-api.com/json/");
            request.Headers.UserAgent.ParseAdd("LEDApp/1.0");

            using var response = await HttpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync();
            var payload = await JsonSerializer.DeserializeAsync<IpApiResponse>(stream, SerializerOptions);

            if (payload is null || !payload.IsSuccess)
            {
                throw new InvalidOperationException(payload?.Message ?? "Unknown IP API error.");
            }

            return new GeoCoordinate(payload.Lat, payload.Lon);
        }
        catch
        {
            var fallback = AppSettings.Default;
            return new GeoCoordinate(fallback.Latitude, fallback.Longitude);
        }
    }

    public SunTimes GetSunTimes(double lat, double lon, DateTime date)
    {
        try
        {
            var targetDate = date.Date;
            var sunriseUtc = CalculateSunTimeUtc(lat, lon, targetDate, isSunrise: true);
            var sunsetUtc = CalculateSunTimeUtc(lat, lon, targetDate, isSunrise: false);

            var localTimeZone = TimeZoneInfo.Local;
            DateTime? sunriseLocal = sunriseUtc.HasValue
                ? TimeZoneInfo.ConvertTimeFromUtc(sunriseUtc.Value, localTimeZone)
                : null;
            DateTime? sunsetLocal = sunsetUtc.HasValue
                ? TimeZoneInfo.ConvertTimeFromUtc(sunsetUtc.Value, localTimeZone)
                : null;

            return new SunTimes(sunriseLocal, sunsetLocal);
        }
        catch
        {
            return SunTimes.Empty;
        }
    }

    private static DateTime? CalculateSunTimeUtc(double lat, double lon, DateTime date, bool isSunrise)
    {
        const double zenith = 90.833;

        var dayOfYear = date.DayOfYear;
        var lngHour = lon / 15.0;
        var t = dayOfYear + ((isSunrise ? 6.0 : 18.0) - lngHour) / 24.0;

        var m = (0.9856 * t) - 3.289;
        var l = m + (1.916 * SinDeg(m)) + (0.020 * SinDeg(2.0 * m)) + 282.634;
        l = NormalizeDegrees(l);

        var ra = RadToDeg(Math.Atan(0.91764 * Math.Tan(DegToRad(l))));
        ra = NormalizeDegrees(ra);

        var lQuadrant = Math.Floor(l / 90.0) * 90.0;
        var raQuadrant = Math.Floor(ra / 90.0) * 90.0;
        ra += lQuadrant - raQuadrant;
        ra /= 15.0;

        var sinDec = 0.39782 * SinDeg(l);
        var cosDec = Math.Cos(Math.Asin(sinDec));

        var cosH = (CosDeg(zenith) - (sinDec * SinDeg(lat))) / (cosDec * CosDeg(lat));
        if (cosH > 1.0 || cosH < -1.0)
        {
            return null;
        }

        var h = isSunrise ? 360.0 - RadToDeg(Math.Acos(cosH)) : RadToDeg(Math.Acos(cosH));
        h /= 15.0;

        var tLocal = h + ra - (0.06571 * t) - 6.622;
        var ut = tLocal - lngHour;
        ut = NormalizeHours(ut);

        return new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Utc)
            .AddHours(ut);
    }

    private static double SinDeg(double degrees) => Math.Sin(DegToRad(degrees));

    private static double CosDeg(double degrees) => Math.Cos(DegToRad(degrees));

    private static double DegToRad(double degrees) => degrees * Math.PI / 180.0;

    private static double RadToDeg(double radians) => radians * 180.0 / Math.PI;

    private static double NormalizeDegrees(double degrees)
    {
        degrees %= 360.0;
        if (degrees < 0.0)
        {
            degrees += 360.0;
        }

        return degrees;
    }

    private static double NormalizeHours(double hours)
    {
        hours %= 24.0;
        if (hours < 0.0)
        {
            hours += 24.0;
        }

        return hours;
    }

    private sealed record IpApiResponse(string Status, double Lat, double Lon, string? Message)
    {
        public bool IsSuccess => Status.Equals("success", StringComparison.OrdinalIgnoreCase);
    }
}
