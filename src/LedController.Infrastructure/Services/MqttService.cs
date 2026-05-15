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
    private readonly ISchedulerService _schedulerService;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly object _reconnectLock = new();

    private IMqttClient? _client;
    private CancellationTokenSource? _reconnectCts;
    private Task? _reconnectTask;
    private bool _stopRequested;

    public MqttService(IConfigService configService, IBleService bleService, ISchedulerService schedulerService)
    {
        _configService = configService;
        _bleService = bleService;
        _schedulerService = schedulerService;
        _schedulerService.DeviceStateChanged += OnSchedulerDeviceStateChanged;
    }

    public bool IsRunning { get; private set; }

    public async Task StartAsync()
    {
        _stopRequested = false;
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

            EnsureClient();
            var client = _client ?? throw new InvalidOperationException("MQTT client not initialized.");

            var optionsBuilder = new MqttClientOptionsBuilder()
                .WithClientId($"LedController-{Guid.NewGuid()}")
                .WithTcpServer(settings.Host, settings.Port);

            if (!string.IsNullOrWhiteSpace(settings.Username))
            {
                optionsBuilder = optionsBuilder.WithCredentials(settings.Username, settings.Password);
            }

            var options = optionsBuilder.Build();
            await client.ConnectAsync(options, CancellationToken.None);
            await client.SubscribeAsync(CommandTopic, MqttQualityOfServiceLevel.AtMostOnce);
            IsRunning = true;
            await PublishAllStatesAsync(config.SavedDevices);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MQTT] Start failed: {ex.Message}");
            IsRunning = false;
            ScheduleReconnect();
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task StopAsync()
    {
        _stopRequested = true;
        CancelReconnect();
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

    private void EnsureClient()
    {
        if (_client is not null)
        {
            return;
        }

        var factory = new MqttFactory();
        _client = factory.CreateMqttClient();

        _client.ApplicationMessageReceivedAsync += HandleMessageAsync;
        _client.DisconnectedAsync += _ =>
        {
            IsRunning = false;
            if (!_stopRequested)
            {
                ScheduleReconnect();
            }

            return Task.CompletedTask;
        };
    }

    private void ScheduleReconnect()
    {
        lock (_reconnectLock)
        {
            if (_stopRequested)
            {
                return;
            }

            if (_reconnectTask is { IsCompleted: false })
            {
                return;
            }

            _reconnectCts?.Cancel();
            _reconnectCts = new CancellationTokenSource();
            var token = _reconnectCts.Token;
            _reconnectTask = Task.Run(() => ReconnectLoopAsync(token), token);
        }
    }

    private void CancelReconnect()
    {
        lock (_reconnectLock)
        {
            _reconnectCts?.Cancel();
            _reconnectCts = null;
            _reconnectTask = null;
        }
    }

    private async Task ReconnectLoopAsync(CancellationToken token)
    {
        var delay = TimeSpan.FromSeconds(5);
        while (!token.IsCancellationRequested)
        {
            try
            {
                var config = await _configService.LoadConfigAsync();
                var settings = config.Settings?.Mqtt ?? MqttSettings.Default;
                if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.Host))
                {
                    return;
                }

                if (await TryConnectAsync(settings, token))
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MQTT] Reconnect failed: {ex.Message}");
            }

            try
            {
                await Task.Delay(delay, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
        }
    }

    private async Task<bool> TryConnectAsync(MqttSettings settings, CancellationToken token)
    {
        await _sync.WaitAsync(token);
        try
        {
            if (IsRunning && _client?.IsConnected == true)
            {
                return true;
            }

            EnsureClient();

            var optionsBuilder = new MqttClientOptionsBuilder()
                .WithClientId($"LedController-{Guid.NewGuid()}")
                .WithTcpServer(settings.Host, settings.Port);

            if (!string.IsNullOrWhiteSpace(settings.Username))
            {
                optionsBuilder = optionsBuilder.WithCredentials(settings.Username, settings.Password);
            }

            var options = optionsBuilder.Build();
            await _client!.ConnectAsync(options, token);
            await _client.SubscribeAsync(CommandTopic, MqttQualityOfServiceLevel.AtMostOnce);
            IsRunning = true;
            var config = await _configService.LoadConfigAsync();
            await PublishAllStatesAsync(config.SavedDevices);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MQTT] Connect failed: {ex.Message}");
            IsRunning = false;
            return false;
        }
        finally
        {
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

            var requiresConnection = RequiresDeviceConnection(payload);
            if (requiresConnection && !device.IsConnected)
            {
                try
                {
                    await _bleService.ConnectAsync(device);
                    if (device.IsConnected)
                    {
                        await PersistConnectedDeviceIdentityAsync(device, config);
                    }
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
        var scheduleChanged = false;

        if (payload.ScheduleEnabled.HasValue)
        {
            device.ScheduleEnabled = payload.ScheduleEnabled.Value;
            _schedulerService.SetDeviceScheduleEnabled(device.Id, device.ScheduleEnabled);
            _schedulerService.MarkDeviceStateDirty(device.Id);
            await PersistDeviceConfigAsync(
                device,
                target => target.ScheduleEnabled = device.ScheduleEnabled,
                "schedule state");
            scheduleChanged = true;
        }

        if (wantsOff)
        {
            await _bleService.SendCommandAsync(device, LedColor.OffCommandBytes);
            device.IsOn = false;
            device.CurrentColor = LedColor.Off;
            _schedulerService.MarkDeviceStateDirty(device.Id);
            return;
        }

        if (!string.IsNullOrWhiteSpace(payload.Color))
        {
            var color = new LedColor("MQTT", payload.Color);
            await _bleService.SendCommandAsync(device, color.ToCommandBytes());
            device.CurrentColor = color;
            device.IsOn = true;
            _schedulerService.MarkDeviceStateDirty(device.Id);
        }
        else if (wantsOn)
        {
            var fallback = device.CurrentColor ?? new LedColor("Feher", "#ffffff");
            await _bleService.SendCommandAsync(device, fallback.ToCommandBytes());
            device.CurrentColor = fallback;
            device.IsOn = true;
            _schedulerService.MarkDeviceStateDirty(device.Id);
        }

        if (payload.Brightness.HasValue)
        {
            var brightness = Math.Clamp(payload.Brightness.Value, 0, 100);
            var command = BuildBrightnessCommand(brightness);
            await _bleService.SendCommandAsync(device, command);
            device.Brightness = brightness;
            device.IsOn = true;
            _schedulerService.MarkDeviceStateDirty(device.Id);
            await PersistDeviceConfigAsync(
                device,
                target => target.Brightness = brightness,
                "brightness");
        }

        if (scheduleChanged)
        {
            _schedulerService.MarkDeviceStateDirty(device.Id);
        }
    }

    public async Task PublishStateAsync(LedDevice device)
    {
        if (device is null || _client is null || !_client.IsConnected)
        {
            return;
        }

        var deviceId = LedDeviceIdentity.GetStableId(device);
        var topic = $"{StateTopicPrefix}{deviceId}/state";
        var payload = new MqttStatePayload
        {
            State = device.IsOn ? "ON" : "OFF",
            Color = device.CurrentColor?.NormalizedHex ?? "#000000",
            Brightness = device.Brightness,
            ScheduleEnabled = device.ScheduleEnabled
        };

        var json = JsonSerializer.Serialize(payload, SerializerOptions);
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(json)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
            .WithRetainFlag(true)
            .Build();

        await _client.PublishAsync(message);
    }

    private async Task PublishAllStatesAsync(IEnumerable<LedDevice> devices)
    {
        foreach (var device in devices)
        {
            await PublishStateAsync(device);
        }
    }

    private static LedDevice? FindDevice(IReadOnlyList<LedDevice> devices, string deviceId)
    {
        return devices.FirstOrDefault(d => LedDeviceIdentity.Matches(d, deviceId));
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

    private async Task PersistConnectedDeviceIdentityAsync(LedDevice device, AppConfig config)
    {
        try
        {
            var devices = config.SavedDevices.ToList();
            var target = devices.FirstOrDefault(d => LedDeviceIdentity.Matches(d, device));
            if (target is null)
            {
                target = device;
                devices.Add(target);
            }

            if (!string.IsNullOrWhiteSpace(device.Name))
            {
                target.Name = device.Name;
            }

            if (!string.IsNullOrWhiteSpace(device.MacAddress))
            {
                target.MacAddress = device.MacAddress;
            }

            if (!string.IsNullOrWhiteSpace(device.DeviceIdentifier))
            {
                target.DeviceIdentifier = device.DeviceIdentifier;
            }

            var updated = new AppConfig(devices, config.Profiles, config.Settings);
            await _configService.SaveConfigAsync(updated);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MQTT] Device identity save failed: {ex.Message}");
        }
    }

    private async Task PersistDeviceConfigAsync(LedDevice device, Action<LedDevice> update, string operationName)
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            var devices = config.SavedDevices.ToList();
            var target = devices.FirstOrDefault(d => LedDeviceIdentity.Matches(d, device));
            if (target is null)
            {
                target = device;
                devices.Add(target);
            }

            update(target);

            var updated = new AppConfig(devices, config.Profiles, config.Settings);
            await _configService.SaveConfigAsync(updated);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MQTT] Failed to persist {operationName}: {ex.Message}");
        }
    }

    private void OnSchedulerDeviceStateChanged(LedDevice device)
    {
        _ = PublishStateAsync(device);
    }

    private static bool RequiresDeviceConnection(MqttCommandPayload payload)
    {
        var state = payload.State?.Trim();
        return string.Equals(state, "ON", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(state, "OFF", StringComparison.OrdinalIgnoreCase) ||
               !string.IsNullOrWhiteSpace(payload.Color) ||
               payload.Brightness.HasValue;
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
        public bool? ScheduleEnabled { get; set; }
    }

    private sealed class MqttStatePayload
    {
        public string State { get; set; } = "OFF";
        public string Color { get; set; } = "#000000";
        public int Brightness { get; set; }
        public bool ScheduleEnabled { get; set; }
    }
}
