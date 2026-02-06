using LedController.Core.Interfaces;
using LedController.Core.Models;
using System.Collections.Concurrent;

namespace LedController.Infrastructure.Services;

public sealed class SchedulerService : ISchedulerService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LocationCacheDuration = TimeSpan.FromHours(1);

    private readonly object _sync = new();
    private readonly object _stateLock = new();
    private readonly IBleService _bleService;
    private readonly IConfigService _configService;
    private readonly ILocationService _locationService;

    private readonly ConcurrentDictionary<Guid, DeviceState> _deviceStates = new();

    private GeoCoordinate? _cachedCoordinates;
    private DateTime _lastLocationFetchUtc = DateTime.MinValue;

    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;
    private Task? _runner;

    public SchedulerService(
        IBleService bleService,
        IConfigService configService,
        ILocationService locationService)
    {
        _bleService = bleService;
        _configService = configService;
        _locationService = locationService;
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_runner is { IsCompleted: false })
            {
                return;
            }

            _cts = new CancellationTokenSource();
            _timer = new PeriodicTimer(Interval);
            _runner = RunAsync(_cts.Token);
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        PeriodicTimer? timer;

        lock (_sync)
        {
            cts = _cts;
            timer = _timer;
            _cts = null;
            _timer = null;
            _runner = null;
        }

        try
        {
            cts?.Cancel();
        }
        catch
        {
        }

        try
        {
            timer?.Dispose();
        }
        catch
        {
        }
    }

    public void MarkDeviceStateDirty(Guid deviceId)
    {
        var state = _deviceStates.GetOrAdd(deviceId, _ => new DeviceState());
        lock (_stateLock)
        {
            state.ForceRefresh = true;
        }
    }

    public void SetDeviceScheduleEnabled(Guid deviceId, bool enabled)
    {
        var state = _deviceStates.GetOrAdd(deviceId, _ => new DeviceState());
        lock (_stateLock)
        {
            state.ScheduleEnabledOverride = enabled;
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            try
            {
                await TickAsync(token);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Scheduler] Tick failed: {ex}");
            }

            if (_timer is null)
            {
                return;
            }

            while (await _timer.WaitForNextTickAsync(token))
            {
                try
                {
                    await TickAsync(token);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Scheduler] Tick failed: {ex}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Scheduler] Unexpected error: {ex}");
        }
    }

    private async Task TickAsync(CancellationToken token)
    {
        var now = DateTime.Now;

        AppConfig config;
        try
        {
            config = await _configService.LoadConfigAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Scheduler] Failed to load config: {ex}");
            return;
        }

        if (config.SavedDevices.Count == 0)
        {
            return;
        }

        GeoCoordinate coordinates;
        try
        {
            var settings = config.Settings ?? AppSettings.Default;
            coordinates = await ResolveCoordinatesAsync(settings);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Scheduler] Failed to resolve location: {ex}");
            var fallback = AppSettings.Default;
            coordinates = new GeoCoordinate(fallback.Latitude, fallback.Longitude);
        }

        SunTimes todaySunTimes;
        SunTimes yesterdaySunTimes;
        try
        {
            todaySunTimes = _locationService.GetSunTimes(coordinates.Latitude, coordinates.Longitude, now);
            yesterdaySunTimes = _locationService.GetSunTimes(coordinates.Latitude, coordinates.Longitude, now.AddDays(-1));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Scheduler] Failed to compute sun times: {ex}");
            todaySunTimes = SunTimes.Empty;
            yesterdaySunTimes = SunTimes.Empty;
        }

        foreach (var device in config.SavedDevices)
        {
            token.ThrowIfCancellationRequested();

            var state = _deviceStates.GetOrAdd(device.Id, _ => new DeviceState());

            var profilesForDevice = device.Profiles.Count > 0
                ? device.Profiles.ToList()
                : config.Profiles;

            var hasActiveProfiles = profilesForDevice.Any(p => p.IsActive);
            bool? scheduleOverride;
            lock (_stateLock)
            {
                scheduleOverride = state.ScheduleEnabledOverride;
            }

            if (scheduleOverride == false || !hasActiveProfiles)
            {
                continue;
            }

            var desiredColor = ResolveDesiredColor(profilesForDevice, now, todaySunTimes, yesterdaySunTimes);

            var forceRefresh = false;
            lock (_stateLock)
            {
                forceRefresh = state.ForceRefresh;
                state.ForceRefresh = false;
            }

            if (desiredColor is not null)
            {
                var needsUpdate = forceRefresh ||
                                  !state.IsOn ||
                                  string.IsNullOrWhiteSpace(state.ColorHex) ||
                                  !string.Equals(state.ColorHex, desiredColor.NormalizedHex, StringComparison.OrdinalIgnoreCase);

                if (needsUpdate)
                {
                    try
                    {
                        await _bleService.SendCommandAsync(device, desiredColor.ToCommandBytes());
                        device.IsOn = true;
                        device.CurrentColor = desiredColor;
                        state.IsOn = true;
                        state.ColorHex = desiredColor.NormalizedHex;
                    }
                    catch (Exception ex)
                    {
                        device.IsConnected = false;
                        Console.WriteLine($"[Scheduler] BLE send failed for {device.Name}: {ex.Message}");
                    }
                }
            }
            else if (state.IsOn || forceRefresh)
            {
                try
                {
                    await _bleService.SendCommandAsync(device, LedColor.OffCommandBytes);
                    device.IsOn = false;
                    device.CurrentColor = LedColor.Off;
                    state.IsOn = false;
                    state.ColorHex = LedColor.Off.NormalizedHex;
                }
                catch (Exception ex)
                {
                    device.IsConnected = false;
                    Console.WriteLine($"[Scheduler] BLE off failed for {device.Name}: {ex.Message}");
                }
            }
        }
    }

    private async Task<GeoCoordinate> ResolveCoordinatesAsync(AppSettings settings)
    {
        if (settings.Latitude != 0 || settings.Longitude != 0)
        {
            return new GeoCoordinate(settings.Latitude, settings.Longitude);
        }

        var now = DateTime.UtcNow;
        if (_cachedCoordinates is not null && now - _lastLocationFetchUtc < LocationCacheDuration)
        {
            return _cachedCoordinates;
        }

        var coords = await _locationService.GetCurrentLocationAsync();
        _cachedCoordinates = coords;
        _lastLocationFetchUtc = now;
        return coords;
    }

    private static LedColor? ResolveDesiredColor(
        IReadOnlyList<ScheduleProfile> profiles,
        DateTime now,
        SunTimes todaySunTimes,
        SunTimes yesterdaySunTimes)
    {
        var intervals = new List<ScheduleInterval>();
        var today = now.Date;
        var yesterday = today.AddDays(-1);

        foreach (var profile in profiles)
        {
            if (!profile.IsActive)
            {
                continue;
            }

            var yesterdaySchedule = profile.GetSchedule(yesterday.DayOfWeek);
            var todaySchedule = profile.GetSchedule(today.DayOfWeek);

            AddInterval(intervals, yesterdaySchedule, yesterday, yesterdaySunTimes);
            AddInterval(intervals, todaySchedule, today, todaySunTimes);
        }

        if (intervals.Count == 0)
        {
            return null;
        }

        intervals.Sort((a, b) => a.Start.CompareTo(b.Start));
        foreach (var interval in intervals)
        {
            if (interval.Start <= now && now < interval.End)
            {
                return interval.Color;
            }
        }

        return null;
    }

    private static void AddInterval(
        ICollection<ScheduleInterval> intervals,
        DailySchedule? schedule,
        DateTime date,
        SunTimes sunTimes)
    {
        if (schedule?.TargetColor is null)
        {
            return;
        }

        DateTime? start = null;
        DateTime? end = null;

        var onEvents = new List<DateTime>();
        var offEvents = new List<DateTime>();

        if (schedule.SunriseEnabled && sunTimes.Sunrise is not null)
        {
            var sunriseTime = sunTimes.Sunrise.Value.AddMinutes(schedule.SunriseOffset);
            if (schedule.SunriseTurnsOn)
            {
                onEvents.Add(sunriseTime);
            }
            else
            {
                offEvents.Add(sunriseTime);
            }
        }

        if (schedule.SunsetEnabled && sunTimes.Sunset is not null)
        {
            var sunsetTime = sunTimes.Sunset.Value.AddMinutes(schedule.SunsetOffset);
            if (schedule.SunsetTurnsOn)
            {
                onEvents.Add(sunsetTime);
            }
            else
            {
                offEvents.Add(sunsetTime);
            }
        }

        if (onEvents.Count == 0 && schedule.FixedOnTime is not null)
        {
            onEvents.Add(date.Add(schedule.FixedOnTime.Value));
        }

        if (offEvents.Count == 0 && schedule.FixedOffTime is not null)
        {
            offEvents.Add(date.Add(schedule.FixedOffTime.Value));
        }

        if (onEvents.Count > 0)
        {
            start = onEvents.Min();
        }

        if (offEvents.Count > 0 && start is not null)
        {
            DateTime? candidate = null;
            foreach (var off in offEvents)
            {
                var adjusted = off <= start.Value ? off.AddDays(1) : off;
                if (candidate is null || adjusted < candidate.Value)
                {
                    candidate = adjusted;
                }
            }

            end = candidate;
        }

        if (start is null || end is null)
        {
            return;
        }

        if (end <= start)
        {
            end = end.Value.AddDays(1);
        }

        intervals.Add(new ScheduleInterval(start.Value, end.Value, schedule.TargetColor));
    }

    private sealed record ScheduleInterval(DateTime Start, DateTime End, LedColor Color);

    private sealed class DeviceState
    {
        public bool IsOn { get; set; }
        public string? ColorHex { get; set; }
        public bool ForceRefresh { get; set; }
        public bool? ScheduleEnabledOverride { get; set; }
    }
}
