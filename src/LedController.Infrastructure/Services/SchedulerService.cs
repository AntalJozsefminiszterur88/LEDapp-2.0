using LedController.Core.Interfaces;
using LedController.Core.Models;
using System.Collections.Concurrent;

namespace LedController.Infrastructure.Services;

public sealed class SchedulerService : ISchedulerService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LocationCacheDuration = TimeSpan.FromHours(1);
    private static readonly TimeSpan BaseReconnectDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ManualOverrideDuration = TimeSpan.FromHours(6);

    public event Action<LedDevice>? DeviceStateChanged;

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

    public void SetManualColorOverride(Guid deviceId, LedColor color)
    {
        var state = _deviceStates.GetOrAdd(deviceId, _ => new DeviceState());
        lock (_stateLock)
        {
            state.ManualOverrideColor = color;
            state.ManualOverrideExpiresUtc = DateTime.UtcNow.Add(ManualOverrideDuration);
            state.ForceRefresh = true;
        }
    }

    public void ClearManualColorOverride(Guid deviceId)
    {
        var state = _deviceStates.GetOrAdd(deviceId, _ => new DeviceState());
        lock (_stateLock)
        {
            state.ManualOverrideColor = null;
            state.ManualOverrideExpiresUtc = null;
        }
    }

    public async Task SetDeviceScheduleEnabledAsync(Guid deviceId, bool enabled)
    {
        var state = _deviceStates.GetOrAdd(deviceId, _ => new DeviceState());
        bool hadOverride;
        lock (_stateLock)
        {
            hadOverride = state.ManualOverrideColor is not null;
            state.ScheduleEnabledOverride = enabled;
            state.ManualOverrideColor = null;
            state.ManualOverrideExpiresUtc = null;
            state.ForceRefresh = true;
        }

        if (!hadOverride)
        {
            return;
        }

        // Volt aktiv kezi felulbiralas - most, hogy torolve lett, azonnal
        // visszaallitjuk arra a szinre, amit az utemezo ETTOL FUGGETLENUL,
        // pusztan az idorendi szabalyok alapjan adna most, meg akkor is, ha
        // a kapcsolo epp kikapcsolta az utemezest (igy az csak "megfagy"
        // ezen a szinen, nem marad az elozo kezi felulbiralas szinen).
        try
        {
            var config = await _configService.LoadConfigAsync();
            var device = config.SavedDevices.FirstOrDefault(d => d.Id == deviceId);
            if (device is null)
            {
                return;
            }

            var profilesForDevice = device.Profiles.Count > 0
                ? device.Profiles.ToList()
                : config.Profiles;

            var settings = config.Settings ?? AppSettings.Default;
            var coordinates = await ResolveCoordinatesAsync(settings);
            var now = DateTime.Now;
            var todaySunTimes = _locationService.GetSunTimes(coordinates.Latitude, coordinates.Longitude, now);
            var yesterdaySunTimes = _locationService.GetSunTimes(coordinates.Latitude, coordinates.Longitude, now.AddDays(-1));
            var desiredColor = ResolveDesiredColor(profilesForDevice, now, todaySunTimes, yesterdaySunTimes);

            var command = desiredColor?.ToCommandBytes() ?? LedColor.OffCommandBytes;
            if (await SendScheduledCommandAsync(device, state, command, "revert-after-toggle"))
            {
                device.IsOn = desiredColor is not null;
                device.CurrentColor = desiredColor ?? LedColor.Off;
                lock (_stateLock)
                {
                    state.IsOn = device.IsOn;
                    state.ColorHex = (desiredColor ?? LedColor.Off).NormalizedHex;
                }

                DeviceStateChanged?.Invoke(device);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Scheduler] Failed to resolve revert color after toggle: {ex.Message}");
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

            var forceRefresh = false;
            lock (_stateLock)
            {
                forceRefresh = state.ForceRefresh;
                state.ForceRefresh = false;
            }

            var scheduleControlEnabled = scheduleOverride ?? device.ScheduleEnabled;
            var schedulingEnabled = scheduleControlEnabled && hasActiveProfiles;

            LedColor? manualOverrideColor = null;
            lock (_stateLock)
            {
                if (state.ManualOverrideExpiresUtc is { } expiresUtc && DateTime.UtcNow >= expiresUtc)
                {
                    // Lejart a kezi felulbiralas - automatikusan visszaall az utemezore.
                    state.ManualOverrideColor = null;
                    state.ManualOverrideExpiresUtc = null;
                    state.ForceRefresh = true;
                }

                manualOverrideColor = state.ManualOverrideColor;
            }

            var desiredColor = schedulingEnabled
                ? (manualOverrideColor ?? ResolveDesiredColor(profilesForDevice, now, todaySunTimes, yesterdaySunTimes))
                : null;

            if (schedulingEnabled && !device.IsConnected && !device.IsConnecting)
            {
                await EnsureDeviceConnectedAsync(
                    device,
                    state,
                    desiredColor is null
                        ? "connection maintenance"
                        : $"maintain {desiredColor.NormalizedHex}");
            }

            if (desiredColor is not null)
            {
                var needsUpdate = forceRefresh ||
                                  !state.IsOn ||
                                  string.IsNullOrWhiteSpace(state.ColorHex) ||
                                  !string.Equals(state.ColorHex, desiredColor.NormalizedHex, StringComparison.OrdinalIgnoreCase);

                if (needsUpdate)
                {
                    var action = $"color {desiredColor.NormalizedHex}";
                    if (await SendScheduledCommandAsync(device, state, desiredColor.ToCommandBytes(), action))
                    {
                        device.IsOn = true;
                        device.CurrentColor = desiredColor;
                        state.IsOn = true;
                        state.ColorHex = desiredColor.NormalizedHex;
                        DeviceStateChanged?.Invoke(device);
                    }
                }
            }
            else if (schedulingEnabled)
            {
                var needsUpdate = forceRefresh ||
                                  state.IsOn ||
                                  !string.Equals(state.ColorHex, LedColor.Off.NormalizedHex, StringComparison.OrdinalIgnoreCase);

                if (needsUpdate && await SendScheduledCommandAsync(device, state, LedColor.OffCommandBytes, "off"))
                {
                    device.IsOn = false;
                    device.CurrentColor = LedColor.Off;
                    state.IsOn = false;
                    state.ColorHex = LedColor.Off.NormalizedHex;
                    DeviceStateChanged?.Invoke(device);
                }
            }


        }
    }

    private async Task<bool> SendScheduledCommandAsync(
        LedDevice device,
        DeviceState state,
        byte[] command,
        string action)
    {
        if (!await EnsureDeviceConnectedAsync(device, state, action))
        {
            return false;
        }

        try
        {
            BleLog.Info($"[Scheduler] Sending scheduled {action} command for {device.PrimaryName}.");
            await _bleService.SendCommandAsync(device, command);
            BleLog.Info($"[Scheduler] Scheduled {action} send success for {device.PrimaryName}.");
            return true;
        }
        catch (Exception ex)
        {
            device.IsConnected = false;
            lock (_stateLock)
            {
                state.ForceRefresh = true;
            }

            BleLog.Exception($"[Scheduler] Scheduled {action} send failed for {device.PrimaryName}. Retrying after reconnect.", ex);

            if (!await EnsureDeviceConnectedAsync(device, state, $"{action} retry"))
            {
                return false;
            }

            try
            {
                await _bleService.SendCommandAsync(device, command);
                BleLog.Info($"[Scheduler] Scheduled {action} send recovered after reconnect for {device.PrimaryName}.");
                return true;
            }
            catch (Exception retryEx)
            {
                device.IsConnected = false;
                lock (_stateLock)
                {
                    state.ForceRefresh = true;
                }

                BleLog.Exception($"[Scheduler] Scheduled {action} retry failed for {device.PrimaryName}.", retryEx);
                return false;
            }
        }
    }

    private async Task<bool> EnsureDeviceConnectedAsync(LedDevice device, DeviceState state, string reason)
    {
        if (device.IsConnected || await _bleService.IsDeviceConnectedAsync(device))
        {
            return true;
        }

        if (device.IsConnecting)
        {
            lock (_stateLock)
            {
                state.ForceRefresh = true;
            }

            return false;
        }

        var now = DateTime.UtcNow;
        if (now < state.NextReconnectAllowedUtc)
        {
            return false;
        }

        device.IsConnecting = true;
        try
        {
            BleLog.Info(
                $"[Scheduler] Reconnect start for {device.PrimaryName}. Reason={reason}; Address={device.MacAddress}; DeviceIdentifier={device.DeviceIdentifier}");

            await _bleService.ConnectAsync(device);
            if (!device.IsConnected)
            {
                lock (_stateLock)
                {
                    state.ForceRefresh = true;
                }

                BleLog.Error($"[Scheduler] Reconnect finished without an active connection for {device.PrimaryName}.");
                return false;
            }

            lock (_stateLock)
            {
                state.ForceRefresh = true;
            }

            BleLog.Info($"[Scheduler] Reconnect success for {device.PrimaryName}.");
            state.ConsecutiveConnectFailures = 0;
            state.NextReconnectAllowedUtc = DateTime.MinValue;
            return true;
        }
        catch (Exception ex)
        {
            device.IsConnected = false;
            lock (_stateLock)
            {
                state.ForceRefresh = true;
            }

            BleLog.Exception($"[Scheduler] Reconnect failed for {device.PrimaryName}.", ex);
            ApplyReconnectBackoff(state, device);
            return false;
        }
        finally
        {
            device.IsConnecting = false;
        }
    }

    private static void ApplyReconnectBackoff(DeviceState state, LedDevice device)
    {
        state.ConsecutiveConnectFailures++;
        var multiplier = Math.Pow(2, Math.Min(state.ConsecutiveConnectFailures - 1, 5));
        var delaySeconds = Math.Min(BaseReconnectDelay.TotalSeconds * multiplier, MaxReconnectDelay.TotalSeconds);
        var delay = TimeSpan.FromSeconds(delaySeconds);
        state.NextReconnectAllowedUtc = DateTime.UtcNow.Add(delay);

        BleLog.Info(
            $"[Scheduler] Backoff scheduled for {device.PrimaryName}. FailureCount={state.ConsecutiveConnectFailures}; " +
            $"NextAttemptUtc={state.NextReconnectAllowedUtc:O}; DelaySeconds={delay.TotalSeconds:0}");
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
        public LedColor? ManualOverrideColor { get; set; }
        public DateTime? ManualOverrideExpiresUtc { get; set; }
        public int ConsecutiveConnectFailures { get; set; }
        public DateTime NextReconnectAllowedUtc { get; set; } = DateTime.MinValue;
    }
}
