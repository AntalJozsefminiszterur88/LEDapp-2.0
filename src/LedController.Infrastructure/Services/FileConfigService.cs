using System.Text.Json;
using System.Text.Json.Serialization;
using LedController.Core.Interfaces;
using LedController.Core.Models;

namespace LedController.Infrastructure.Services;

public sealed class FileConfigService : IConfigService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(10);

    private readonly string _configPath;
    private readonly string _backupPath;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private AppConfig? _cache;
    private DateTime _cacheTimestampUtc;

    public FileConfigService()
    {
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UMKGL Solutions",
            "LEDapp");

        _configPath = Path.Combine(baseDir, "led_config.json");
        _backupPath = Path.Combine(baseDir, "led_config.bak.json");
    }

    public async Task<AppConfig> LoadConfigAsync()
    {
        var now = DateTime.UtcNow;
        if (_cache is not null && now - _cacheTimestampUtc < CacheDuration)
        {
            return _cache;
        }

        await _sync.WaitAsync();
        try
        {
            now = DateTime.UtcNow;
            if (_cache is not null && now - _cacheTimestampUtc < CacheDuration)
            {
                return _cache;
            }

            EnsureDirectory();

            if (!File.Exists(_configPath))
            {
                var defaultConfig = AppConfig.Empty;
                await SaveConfigInternalAsync(defaultConfig);
                return defaultConfig;
            }

            var config = await ReadConfigAsync(_configPath);
            if (config is null && File.Exists(_backupPath))
            {
                config = await ReadConfigAsync(_backupPath);
            }

            if (config is null)
            {
                config = AppConfig.Empty;
            }

            config = NormalizeSettings(config);
            _cache = config;
            _cacheTimestampUtc = DateTime.UtcNow;
            return config;
        }
        catch
        {
            return _cache ?? AppConfig.Empty;
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task SaveConfigAsync(AppConfig config)
    {
        await _sync.WaitAsync();
        try
        {
            EnsureDirectory();
            var normalized = NormalizeSettings(config);
            await SaveConfigInternalAsync(normalized);
            _cache = normalized;
            _cacheTimestampUtc = DateTime.UtcNow;
        }
        catch
        {
        }
        finally
        {
            _sync.Release();
        }
    }

    private void EnsureDirectory()
    {
        var directory = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private async Task<AppConfig?> ReadConfigAsync(string path)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 4096,
                useAsync: true);

            return await JsonSerializer.DeserializeAsync<AppConfig>(stream, SerializerOptions);
        }
        catch
        {
            return null;
        }
    }

    private async Task SaveConfigInternalAsync(AppConfig config)
    {
        var tempPath = _configPath + ".tmp";

        await using (var stream = new FileStream(
                         tempPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 4096,
                         useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, config, SerializerOptions);
        }

        if (File.Exists(_configPath))
        {
            File.Replace(tempPath, _configPath, _backupPath, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempPath, _configPath, overwrite: true);
        }
    }

    private AppConfig NormalizeSettings(AppConfig config)
    {
        var oldDevices = _cache?.SavedDevices ?? Array.Empty<LedDevice>();
        var devices = config.SavedDevices
            .Select(d => NormalizeDevice(d, oldDevices))
            .ToList();

        var settings = config.Settings ?? AppSettings.Default;
        var mqtt = settings.Mqtt ?? MqttSettings.Default;
        if (!ReferenceEquals(settings.Mqtt, mqtt))
        {
            settings = settings with { Mqtt = mqtt };
        }

        if (!ReferenceEquals(config.Settings, settings) ||
            !ReferenceEquals(config.SavedDevices, devices))
        {
            config = config with { SavedDevices = devices, Settings = settings };
        }

        return config;
    }

    private static LedDevice NormalizeDevice(LedDevice device, IReadOnlyList<LedDevice> oldDevices)
    {
        if (device is null)
        {
            return new LedDevice();
        }

        if (!string.IsNullOrWhiteSpace(device.DeviceIdentifier) || string.IsNullOrWhiteSpace(device.MacAddress))
        {
            return device;
        }

        device.DeviceIdentifier = device.MacAddress;
        return device;
    }
}
