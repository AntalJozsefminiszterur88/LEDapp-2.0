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

    private readonly string _configPath;

    public FileConfigService()
    {
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UMKGL Solutions",
            "LEDapp");

        _configPath = Path.Combine(baseDir, "led_config.json");
    }

    public async Task<AppConfig> LoadConfigAsync()
    {
        try
        {
            EnsureDirectory();

            if (!File.Exists(_configPath))
            {
                var defaultConfig = AppConfig.Empty;
                await SaveConfigAsync(defaultConfig);
                return defaultConfig;
            }

            await using var stream = new FileStream(
                _configPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);

            var config = await JsonSerializer.DeserializeAsync<AppConfig>(stream, SerializerOptions);
            if (config is null)
            {
                return AppConfig.Empty;
            }

            var settings = config.Settings ?? AppSettings.Default;
            var mqtt = settings.Mqtt ?? MqttSettings.Default;
            if (!ReferenceEquals(settings.Mqtt, mqtt))
            {
                settings = settings with { Mqtt = mqtt };
            }

            if (!ReferenceEquals(config.Settings, settings))
            {
                config = config with { Settings = settings };
            }

            return config;
        }
        catch
        {
            return AppConfig.Empty;
        }
    }

    public async Task SaveConfigAsync(AppConfig config)
    {
        try
        {
            EnsureDirectory();

            await using var stream = new FileStream(
                _configPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true);

            await JsonSerializer.SerializeAsync(stream, config, SerializerOptions);
        }
        catch
        {
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
}
