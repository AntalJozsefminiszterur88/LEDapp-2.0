using System.Linq;
using System.Text;
using System.Text.Json;
using LedController.Core.Interfaces;
using LedController.Core.Models;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace LedController.Infrastructure.Services;

public sealed class MqttService : IMqttService
{
    private const string CommandTopic = "ledcontroller/+/set";
    private const string StateTopicPrefix = "ledcontroller/";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IConfigService _configService;
    private readonly IBleService _bleService;
    private readonly SemaphoreSlim _sync = new(1, 1);

    private IMqttClient? _client;

    public MqttService(IConfigService configService, IBleService bleService)
    {
        _configService = configService;
        _bleService = bleService;
    }

    public bool IsRunning { get; private set; }

    public async Task StartAsync()
    {
        await _sync.WaitAsync();
        try
        {
            if (IsRunning)
            {
                return;
            }

            var config = await _configService.LoadConfigAsync();
            var settings = config.Settings?.Mqtt ?? MqttSettings.Default;
            if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.Host))
            {
                return;
            }

            var factory = new MqttFactory();
            _client = factory.CreateMqttClient();

            _client.ApplicationMessageReceivedAsync += HandleMessageAsync;
            _client.DisconnectedAsync += _ =>
            {
                IsRunning = false;
                return Task.CompletedTask;
            };

            var optionsBuilder = new MqttClientOptionsBuilder()
                .WithClientId($"LedController-{Guid.NewGuid()}")
                .WithTcpServer(settings.Host, settings.Port);

            if (!string.IsNullOrWhiteSpace(settings.Username))
            {
                optionsBuilder = optionsBuilder.WithCredentials(settings.Username, settings.Password);
            }

            var options = optionsBuilder.Build();
            await _client.ConnectAsync(options, CancellationToken.None);
            await _client.SubscribeAsync(CommandTopic, MqttQualityOfServiceLevel.AtMostOnce);
            IsRunning = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MQTT] Start failed: {ex.Message}");
            IsRunning = false;
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task StopAsync()
    {
        await _sync.WaitAsync();
        try
        {
            if (_client is not null && _client.IsConnected)
            {
                await _client.DisconnectAsync();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MQTT] Stop failed: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
            _sync.Release();
        }
    }

    private async Task HandleMessageAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        try
        {
            var topic = args.ApplicationMessage.Topic ?? string.Empty;
            var deviceId = ExtractDeviceId(topic);
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return;
            }

            var payloadSegment = args.ApplicationMessage.PayloadSegment;
            var payloadText = payloadSegment.Array is null || payloadSegment.Count == 0
                ? string.Empty
                : Encoding.UTF8.GetString(payloadSegment.Array, payloadSegment.Offset, payloadSegment.Count);

            var payload = JsonSerializer.Deserialize<MqttCommandPayload>(payloadText, SerializerOptions);
            if (payload is null)
            {
                return;
            }

            var config = await _configService.LoadConfigAsync();
            var device = FindDevice(config.SavedDevices, deviceId);
            if (device is null)
            {
                return;
            }

            if (!device.IsConnected)
            {
                try
                {
                    await _bleService.ConnectAsync(device);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MQTT] Connect failed for {deviceId}: {ex.Message}");
                    return;
                }
            }

            await ApplyCommandAsync(device, payload);
            await PublishStateAsync(device);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MQTT] Message handling failed: {ex.Message}");
        }
    }

    private async Task ApplyCommandAsync(LedDevice device, MqttCommandPayload payload)
    {
        var state = payload.State?.Trim();
        var wantsOff = string.Equals(state, "OFF", StringComparison.OrdinalIgnoreCase);
        var wantsOn = string.Equals(state, "ON", StringComparison.OrdinalIgnoreCase);

        if (wantsOff)
        {
            await _bleService.SendCommandAsync(device, LedColor.OffCommandBytes);
            device.IsOn = false;
            device.CurrentColor = LedColor.Off;
            return;
        }

        if (!string.IsNullOrWhiteSpace(payload.Color))
        {
            var color = new LedColor("MQTT", payload.Color);
            await _bleService.SendCommandAsync(device, color.ToCommandBytes());
            device.CurrentColor = color;
            device.IsOn = true;
        }
        else if (wantsOn)
        {
            var fallback = device.CurrentColor ?? new LedColor("Feher", "#ffffff");
            await _bleService.SendCommandAsync(device, fallback.ToCommandBytes());
            device.CurrentColor = fallback;
            device.IsOn = true;
        }

        if (payload.Brightness.HasValue)
        {
            var brightness = Math.Clamp(payload.Brightness.Value, 0, 100);
            var command = BuildBrightnessCommand(brightness);
            await _bleService.SendCommandAsync(device, command);
            device.Brightness = brightness;
            device.IsOn = true;
        }
    }

    private async Task PublishStateAsync(LedDevice device)
    {
        if (_client is null || !_client.IsConnected)
        {
            return;
        }

        var deviceId = string.IsNullOrWhiteSpace(device.MacAddress)
            ? device.Id.ToString()
            : device.MacAddress;
        var topic = $"{StateTopicPrefix}{deviceId}/state";
        var payload = new MqttStatePayload
        {
            State = device.IsOn ? "ON" : "OFF",
            Color = device.CurrentColor?.NormalizedHex ?? "#000000",
            Brightness = device.Brightness
        };

        var json = JsonSerializer.Serialize(payload, SerializerOptions);
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(json)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
            .Build();

        await _client.PublishAsync(message);
    }

    private static LedDevice? FindDevice(IReadOnlyList<LedDevice> devices, string deviceId)
    {
        return devices.FirstOrDefault(d =>
                   string.Equals(d.MacAddress, deviceId, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(d.Id.ToString(), deviceId, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ExtractDeviceId(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            return null;
        }

        var parts = topic.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return null;
        }

        if (!string.Equals(parts[0], "ledcontroller", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!string.Equals(parts[^1], "set", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return parts[1];
    }

    private static byte[] BuildBrightnessCommand(int value)
    {
        var hexValue = value.ToString("X2");
        var commandHex = $"7e0001{hexValue}00000000ef";
        return HexToBytes(commandHex);
    }

    private static byte[] HexToBytes(string hex)
    {
        if (hex.Length % 2 != 0)
        {
            throw new ArgumentException("Hex string must have an even length.", nameof(hex));
        }

        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }

        return bytes;
    }

    private sealed class MqttCommandPayload
    {
        public string? State { get; set; }
        public string? Color { get; set; }
        public int? Brightness { get; set; }
    }

    private sealed class MqttStatePayload
    {
        public string State { get; set; } = "OFF";
        public string Color { get; set; } = "#000000";
        public int Brightness { get; set; }
    }
}
