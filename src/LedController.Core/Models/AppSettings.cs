namespace LedController.Core.Models;

public sealed record AppSettings
{
    public double Latitude { get; init; } = 47.4338;
    public double Longitude { get; init; } = 19.1931;
    public string TimeZone { get; init; } = "UTC+2";
    public bool StartWithWindows { get; init; }
    public MqttSettings Mqtt { get; init; } = MqttSettings.Default;

    public static AppSettings Default { get; } = new();
}
