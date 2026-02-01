using LedController.Core.Interfaces;
using LedController.Core.Models;
using System.Collections.Concurrent;

namespace LedController.Infrastructure.Services;

public sealed class SchedulerService : ISchedulerService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly object _sync = new();
    private readonly IBleService _bleService;
    private readonly IConfigService _configService;
    private readonly ILocationService _locationService;

    private readonly ConcurrentDictionary<Guid, DeviceState> _deviceStates = new();

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

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            await TickAsync(token);

            if (_timer is null)
            {
                return;
            }

            while (await _timer.WaitForNextTickAsync(token))
            {
                await TickAsync(token);
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

        if (config.SavedDevices.Count == 0 || config.Profiles.Count == 0)
        {
            return;
        }

        GeoCoordinate coordinates;
        try
        {
            coordinates = await _locationService.GetCurrentLocationAsync();
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

            var desiredColor = ResolveDesiredColor(profilesForDevice, now, todaySunTimes, yesterdaySunTimes);

            if (desiredColor is not null)
            {
                var needsUpdate = !state.IsOn ||
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
            else if (state.IsOn)
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

        if (schedule.SunriseEnabled)
        {
            if (sunTimes.Sunrise is not null)
            {
                start = sunTimes.Sunrise.Value.AddMinutes(schedule.SunriseOffset);
            }
        }
        else if (schedule.FixedOnTime is not null)
        {
            start = date.Add(schedule.FixedOnTime.Value);
        }

        if (schedule.SunsetEnabled)
        {
            if (sunTimes.Sunset is not null)
            {
                end = sunTimes.Sunset.Value.AddMinutes(schedule.SunsetOffset);
            }
        }
        else if (schedule.FixedOffTime is not null)
        {
            end = date.Add(schedule.FixedOffTime.Value);
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
    }
}
