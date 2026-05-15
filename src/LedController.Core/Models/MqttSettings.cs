namespace LedController.Core.Models;

public sealed record MqttSettings
{
    public bool Enabled { get; init; }
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 1883;
    public string? Username { get; init; }
    public string? Password { get; init; }

    public static MqttSettings Default { get; } = new();
}
