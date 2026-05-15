using Avalonia;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using LedController.Core.Interfaces;
using LedController.Core.Models;
using LedController.Infrastructure.Services;
using LedController.UI.Services;
#if !WINDOWS
using Linux.Bluetooth;
using Linux.Bluetooth.Extensions;
#endif

namespace LedController.UI;

class Program
{
    private const string StartMinimizedArgument = "--start-minimized-to-tray";
    private const string BleScanOnceArgument = "--ble-scan-once";
    private const string BleInspectArgument = "--ble-inspect";
    private const string BleConnectOnceArgument = "--ble-connect-once";
    private const string BleSendColorArgument = "--ble-send-color";
    private const string BleSendOffArgument = "--ble-send-off";
    private const string RestoreLegacySchedulesArgument = "--restore-legacy-schedules";
    private const string AutostartStatusArgument = "--autostart-status";
    private const string AutostartEnableArgument = "--autostart-enable";
    private const string AutostartDisableArgument = "--autostart-disable";
    private static readonly TimeSpan BleInspectScanDuration = TimeSpan.FromSeconds(6);
    private static readonly JsonSerializerOptions LegacyJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly DayOfWeek[] LegacyWeekOrder =
    {
        DayOfWeek.Monday,
        DayOfWeek.Tuesday,
        DayOfWeek.Wednesday,
        DayOfWeek.Thursday,
        DayOfWeek.Friday,
        DayOfWeek.Saturday,
        DayOfWeek.Sunday
    };
    private static readonly Dictionary<string, DayOfWeek> LegacyDayLookup =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Hétfő"] = DayOfWeek.Monday,
            ["Kedd"] = DayOfWeek.Tuesday,
            ["Szerda"] = DayOfWeek.Wednesday,
            ["Csütörtök"] = DayOfWeek.Thursday,
            ["Péntek"] = DayOfWeek.Friday,
            ["Szombat"] = DayOfWeek.Saturday,
            ["Vasárnap"] = DayOfWeek.Sunday,
            ["Monday"] = DayOfWeek.Monday,
            ["Tuesday"] = DayOfWeek.Tuesday,
            ["Wednesday"] = DayOfWeek.Wednesday,
            ["Thursday"] = DayOfWeek.Thursday,
            ["Friday"] = DayOfWeek.Friday,
            ["Saturday"] = DayOfWeek.Saturday,
            ["Sunday"] = DayOfWeek.Sunday
        };
    private static readonly Dictionary<string, string> LegacyColorHex =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Piros"] = "#ff0000",
            ["Zöld"] = "#00ff00",
            ["Kék"] = "#0000ff",
            ["Sárga"] = "#ffff00",
            ["Cián"] = "#00ffff",
            ["Lila"] = "#800080",
            ["Narancs"] = "#ffa500",
            ["Fehér"] = "#ffffff"
        };

    internal static bool StartMinimizedToTray { get; private set; }
    internal static bool ShowSplashOnStartup => !StartMinimizedToTray;

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        StartMinimizedToTray = args.Any(a => string.Equals(a, StartMinimizedArgument, StringComparison.OrdinalIgnoreCase));

        if (args.Any(a => string.Equals(a, BleScanOnceArgument, StringComparison.OrdinalIgnoreCase)))
        {
            RunBleScanOnceAsync().GetAwaiter().GetResult();
            return;
        }

        if (TryGetOptionValue(args, BleInspectArgument, out var inspectTarget))
        {
            RunBleInspectAsync(inspectTarget).GetAwaiter().GetResult();
            return;
        }

        if (TryGetOptionValue(args, BleConnectOnceArgument, out var connectTarget))
        {
            RunBleConnectOnceAsync(connectTarget).GetAwaiter().GetResult();
            return;
        }

        if (TryGetOptionPair(args, BleSendColorArgument, out var colorTarget, out var colorHex))
        {
            RunBleSendColorAsync(colorTarget, colorHex).GetAwaiter().GetResult();
            return;
        }

        if (TryGetOptionValue(args, BleSendOffArgument, out var offTarget))
        {
            RunBleSendColorAsync(offTarget, "#000000").GetAwaiter().GetResult();
            return;
        }

        if (TryGetOptionalOptionValue(args, RestoreLegacySchedulesArgument, out var restoreLegacyPath))
        {
            RunRestoreLegacySchedulesAsync(restoreLegacyPath).GetAwaiter().GetResult();
            return;
        }

        if (args.Any(a => string.Equals(a, AutostartStatusArgument, StringComparison.OrdinalIgnoreCase)))
        {
            RunAutostartStatus();
            return;
        }

        if (args.Any(a => string.Equals(a, AutostartEnableArgument, StringComparison.OrdinalIgnoreCase)))
        {
            RunAutostartToggle(enabled: true);
            return;
        }

        if (args.Any(a => string.Equals(a, AutostartDisableArgument, StringComparison.OrdinalIgnoreCase)))
        {
            RunAutostartToggle(enabled: false);
            return;
        }

        AppLog.Maintenance();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var exception = e.ExceptionObject as Exception ?? new Exception("Unknown unhandled exception.");
            AppLog.Exception("Unhandled exception (AppDomain).", exception);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            if (!AppLog.IsBenignBackgroundException(e.Exception))
            {
                AppLog.Exception("Unobserved task exception.", e.Exception);
            }

            e.SetObserved();
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains("XOpenDisplay", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("Could not connect to X11", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"Fatal Error: Could not initialize GUI display (X11). {ex.Message}");
                AppLog.Exception("GUI display initialization failed (X11). Process will exit.", ex);
            }
            else
            {
                Console.Error.WriteLine($"Fatal Error during startup: {ex.Message}");
                AppLog.Exception("Fatal error during Avalonia startup.", ex);
            }

            Environment.Exit(1);
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static async Task RunBleScanOnceAsync()
    {
        try
        {
            var bleService = CreateBleService();
            var devices = await bleService.ScanForDevicesAsync();
            Console.WriteLine($"BLE devices found: {devices.Count}");

            foreach (var device in devices)
            {
                Console.WriteLine(
                    $"- Name={device.Name}; Mac={device.MacAddress}; Id={device.DeviceIdentifier}; Stable={LedController.Core.Models.LedDeviceIdentity.GetStableId(device)}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"BLE diagnostic scan failed: {ex.Message}");
            throw;
        }
    }

    private static IBleService CreateBleService()
    {
#if WINDOWS
        return new WindowsBleService();
#else
        return new LinuxBleService();
#endif
    }

    private static IStartupService CreateStartupService()
    {
#if WINDOWS
        return new WindowsStartupService();
#else
        return new LinuxStartupService();
#endif
    }

    private static bool TryGetOptionValue(string[] args, string optionName, out string value)
    {
        value = string.Empty;

        for (var i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], optionName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            value = args[i + 1];
            return !string.IsNullOrWhiteSpace(value);
        }

        return false;
    }

    private static bool TryGetOptionPair(string[] args, string optionName, out string firstValue, out string secondValue)
    {
        firstValue = string.Empty;
        secondValue = string.Empty;

        for (var i = 0; i < args.Length - 2; i++)
        {
            if (!string.Equals(args[i], optionName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            firstValue = args[i + 1];
            secondValue = args[i + 2];
            return !string.IsNullOrWhiteSpace(firstValue) && !string.IsNullOrWhiteSpace(secondValue);
        }

        return false;
    }

    private static bool TryGetOptionalOptionValue(string[] args, string optionName, out string value)
    {
        value = string.Empty;

        for (var i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], optionName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[i + 1];
            }

            return true;
        }

        return false;
    }

    private static async Task RunBleInspectAsync(string target)
    {
#if WINDOWS
        Console.Error.WriteLine("BLE inspect mode is intended for Linux diagnostics.");
        await Task.CompletedTask;
#else
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new ArgumentException("BLE inspect target is required.", nameof(target));
        }

        var adapter = (await BlueZManager.GetAdaptersAsync()).FirstOrDefault()
            ?? throw new InvalidOperationException("Bluetooth adapter not found.");
        var device = await ResolveInspectDeviceAsync(adapter, target)
            ?? throw new InvalidOperationException($"Bluetooth device not found for target '{target}'.");

        Console.WriteLine($"Inspect target: {target}");
        await device.ConnectAsync();
        await device.WaitForPropertyValueAsync("Connected", value: true, timeout: TimeSpan.FromSeconds(15));
        await device.WaitForPropertyValueAsync("ServicesResolved", value: true, timeout: TimeSpan.FromSeconds(15));

        var services = await device.GetServicesAsync();
        Console.WriteLine($"Services found: {services.Count}");

        foreach (var service in services)
        {
            var serviceUuid = await service.GetUUIDAsync();
            Console.WriteLine($"[Service] {serviceUuid}");

            var characteristics = await service.GetCharacteristicsAsync();
            foreach (var characteristic in characteristics)
            {
                var characteristicUuid = await characteristic.GetUUIDAsync();
                var flags = await characteristic.GetFlagsAsync();
                Console.WriteLine($"  [Characteristic] {characteristicUuid} Flags={string.Join(",", flags)}");
            }
        }

        await device.DisconnectAsync();
#endif
    }

    private static async Task RunBleConnectOnceAsync(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new ArgumentException("BLE connect target is required.", nameof(target));
        }

        var bleService = CreateBleService();
        var device = new LedController.Core.Models.LedDevice
        {
            Name = target,
            MacAddress = target,
            DeviceIdentifier = target
        };

        Console.WriteLine($"Connect target: {target}");
        await bleService.ConnectAsync(device);
        Console.WriteLine($"Connected: {device.IsConnected}");
        await bleService.DisconnectAsync(device);
        Console.WriteLine($"Disconnected: {!device.IsConnected}");
    }

    private static async Task RunBleSendColorAsync(string target, string colorHex)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new ArgumentException("BLE write target is required.", nameof(target));
        }

        if (string.IsNullOrWhiteSpace(colorHex))
        {
            throw new ArgumentException("BLE color is required.", nameof(colorHex));
        }

        var bleService = CreateBleService();
        var device = new LedController.Core.Models.LedDevice
        {
            Name = target,
            MacAddress = target,
            DeviceIdentifier = target
        };
        var color = new LedController.Core.Models.LedColor("Diagnostic", colorHex);

        Console.WriteLine($"Write target: {target}");
        Console.WriteLine($"Write color: {color.NormalizedHex}");

        await bleService.ConnectAsync(device);
        Console.WriteLine($"Connected: {device.IsConnected}");

        try
        {
            await bleService.SendCommandAsync(device, color.ToCommandBytes());
            Console.WriteLine("Write result: Success");
        }
        finally
        {
            await bleService.DisconnectAsync(device);
            Console.WriteLine($"Disconnected: {!device.IsConnected}");
        }
    }

    private static async Task RunRestoreLegacySchedulesAsync(string legacyPath)
    {
        var resolvedLegacyPath = ResolveLegacySchedulePath(legacyPath);
        if (!File.Exists(resolvedLegacyPath))
        {
            throw new FileNotFoundException("Legacy schedule file not found.", resolvedLegacyPath);
        }

        var importedProfiles = await LoadLegacyProfilesAsync(resolvedLegacyPath);
        if (importedProfiles.Count == 0)
        {
            throw new InvalidOperationException("No legacy schedule profiles were found to restore.");
        }

        var configService = new FileConfigService();
        var config = await configService.LoadConfigAsync();
        var devices = config.SavedDevices.ToList();
        var globalProfiles = importedProfiles
            .Select(CloneProfile)
            .ToList();

        if (devices.Count > 0)
        {
            var target = devices[0];
            target.Profiles = new ObservableCollection<ScheduleProfile>(
                importedProfiles.Select(CloneProfile));

            Console.WriteLine($"Restored device schedules for: {target.PrimaryName} ({target.SecondaryName})");
        }
        else
        {
            Console.WriteLine("No saved device found, restoring legacy schedules to global profiles only.");
        }

        var updatedConfig = new AppConfig(devices, globalProfiles, config.Settings);
        await configService.SaveConfigAsync(updatedConfig);

        Console.WriteLine($"Restored profiles: {importedProfiles.Count}");
        Console.WriteLine($"Legacy source: {resolvedLegacyPath}");
    }

    private static void RunAutostartStatus()
    {
        var startupService = CreateStartupService();
        Console.WriteLine($"Autostart enabled: {startupService.IsEnabled()}");

#if !WINDOWS
        if (startupService is LinuxStartupService linuxStartupService)
        {
            Console.WriteLine($"Desktop entry: {linuxStartupService.DesktopEntryPath}");
            if (File.Exists(linuxStartupService.DesktopEntryPath))
            {
                Console.WriteLine(File.ReadAllText(linuxStartupService.DesktopEntryPath));
            }
        }
#endif
    }

    private static void RunAutostartToggle(bool enabled)
    {
        var startupService = CreateStartupService();
        var success = startupService.SetEnabled(enabled);
        Console.WriteLine($"Autostart set to {enabled}: {success}");
        RunAutostartStatus();
    }

    private static async Task<List<ScheduleProfile>> LoadLegacyProfilesAsync(string path)
    {
        var json = await File.ReadAllTextAsync(path);
        var payload = JsonSerializer.Deserialize<Dictionary<string, LegacyScheduleProfile>>(json, LegacyJsonOptions);
        if (payload is null || payload.Count == 0)
        {
            return new List<ScheduleProfile>();
        }

        var profiles = new List<ScheduleProfile>();
        foreach (var entry in payload)
        {
            profiles.Add(BuildProfileFromLegacy(entry.Key, entry.Value));
        }

        return profiles;
    }

    private static ScheduleProfile BuildProfileFromLegacy(string name, LegacyScheduleProfile legacy)
    {
        var profile = new ScheduleProfile
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Profil" : name,
            IsActive = legacy.Active
        };

        var legacyDays = new Dictionary<DayOfWeek, LegacyScheduleDay>();
        foreach (var entry in legacy.Schedule)
        {
            if (TryResolveLegacyDay(entry.Key, out var day))
            {
                legacyDays[day] = entry.Value;
            }
        }

        foreach (var day in LegacyWeekOrder)
        {
            var schedule = new DailySchedule
            {
                DayOfWeek = day
            };

            if (legacyDays.TryGetValue(day, out var legacyDay))
            {
                schedule.SunriseEnabled = legacyDay.Sunrise;
                schedule.SunriseOffset = legacyDay.SunriseOffset;
                schedule.SunsetEnabled = legacyDay.Sunset;
                schedule.SunsetOffset = legacyDay.SunsetOffset;

                if (!legacyDay.Sunrise)
                {
                    schedule.FixedOnTime = ParseLegacyTime(legacyDay.OnTime);
                }

                if (!legacyDay.Sunset)
                {
                    schedule.FixedOffTime = ParseLegacyTime(legacyDay.OffTime);
                }

                schedule.TargetColor = ResolveLegacyColor(legacyDay.Color);
            }
            else
            {
                schedule.TargetColor = LedColor.Off;
            }

            profile.DailySchedules.Add(schedule);
        }

        return profile;
    }

    private static ScheduleProfile CloneProfile(ScheduleProfile source)
    {
        var clone = new ScheduleProfile
        {
            Name = source.Name,
            IsActive = source.IsActive
        };

        foreach (var schedule in source.DailySchedules)
        {
            clone.DailySchedules.Add(new DailySchedule
            {
                DayOfWeek = schedule.DayOfWeek,
                SunriseEnabled = schedule.SunriseEnabled,
                SunriseTurnsOn = schedule.SunriseTurnsOn,
                SunriseOffset = schedule.SunriseOffset,
                SunsetEnabled = schedule.SunsetEnabled,
                SunsetTurnsOn = schedule.SunsetTurnsOn,
                SunsetOffset = schedule.SunsetOffset,
                FixedOnTime = schedule.FixedOnTime,
                FixedOffTime = schedule.FixedOffTime,
                TargetColor = schedule.TargetColor is null
                    ? null
                    : new LedColor(schedule.TargetColor.Name, schedule.TargetColor.Hex)
            });
        }

        return clone;
    }

    private static string ResolveLegacySchedulePath(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return Path.GetFullPath(value);
        }

        var current = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && !string.IsNullOrWhiteSpace(current); i++)
        {
            if (File.Exists(Path.Combine(current, "LedController.sln"))
                || Directory.Exists(Path.Combine(current, ".git")))
            {
                return Path.Combine(current, "logs", "led_schedule_profiles.json");
            }

            current = Directory.GetParent(current)?.FullName;
        }

        return Path.Combine(AppContext.BaseDirectory, "logs", "led_schedule_profiles.json");
    }

    private static bool TryResolveLegacyDay(string? value, out DayOfWeek day)
    {
        day = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var key = value.Trim();
        if (LegacyDayLookup.TryGetValue(key, out day))
        {
            return true;
        }

        return Enum.TryParse(key, ignoreCase: true, out day);
    }

    private static TimeSpan? ParseLegacyTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (TimeSpan.TryParseExact(trimmed, "hh\\:mm", CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        if (TimeSpan.TryParseExact(trimmed, "h\\:mm", CultureInfo.InvariantCulture, out parsed))
        {
            return parsed;
        }

        return null;
    }

    private static LedColor ResolveLegacyColor(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return LedColor.Off;
        }

        var trimmed = name.Trim();
        if (LegacyColorHex.TryGetValue(trimmed, out var hex))
        {
            return new LedColor(trimmed, hex);
        }

        if (trimmed.StartsWith('#') && (trimmed.Length == 7 || trimmed.Length == 4))
        {
            return new LedColor(trimmed, trimmed);
        }

        return LedColor.Off;
    }

    private sealed class LegacyScheduleProfile
    {
        [JsonPropertyName("active")]
        public bool Active { get; set; }

        [JsonPropertyName("schedule")]
        public Dictionary<string, LegacyScheduleDay> Schedule { get; set; } = new();
    }

    private sealed class LegacyScheduleDay
    {
        [JsonPropertyName("color")]
        public string? Color { get; set; }

        [JsonPropertyName("on_time")]
        public string? OnTime { get; set; }

        [JsonPropertyName("off_time")]
        public string? OffTime { get; set; }

        [JsonPropertyName("sunrise")]
        public bool Sunrise { get; set; }

        [JsonPropertyName("sunrise_offset")]
        public int SunriseOffset { get; set; }

        [JsonPropertyName("sunset")]
        public bool Sunset { get; set; }

        [JsonPropertyName("sunset_offset")]
        public int SunsetOffset { get; set; }
    }

#if !WINDOWS
    private static async Task<Device?> ResolveInspectDeviceAsync(IAdapter1 adapter, string target)
    {
        var device = await adapter.GetDeviceAsync(target);
        if (device is not null)
        {
            return device;
        }

        try
        {
            await adapter.StartDiscoveryAsync();
            await Task.Delay(BleInspectScanDuration);
        }
        finally
        {
            try
            {
                await adapter.StopDiscoveryAsync();
            }
            catch
            {
            }
        }

        var devices = await adapter.GetDevicesAsync();
        foreach (var candidate in devices)
        {
            if (candidate is null)
            {
                continue;
            }

            var address = await candidate.GetAddressAsync();
            if (string.Equals(address, target, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }
#endif
}
