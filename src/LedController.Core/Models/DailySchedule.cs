using System;
using System.Globalization;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LedController.Core.Models;

public partial class DailySchedule : ObservableObject
{
    private bool _syncingSunriseOffset;
    private bool _syncingSunsetOffset;

    [ObservableProperty]
    private DayOfWeek dayOfWeek;

    [ObservableProperty]
    private bool sunriseEnabled;

    [ObservableProperty]
    private bool sunriseTurnsOn = true;

    [ObservableProperty]
    private int sunriseOffset;

    [ObservableProperty]
    [property: JsonIgnore]
    private bool sunriseOffsetIsBefore;

    [ObservableProperty]
    [property: JsonIgnore]
    private int sunriseOffsetMinutes;

    [ObservableProperty]
    private bool sunsetEnabled;

    [ObservableProperty]
    private bool sunsetTurnsOn = false;

    [ObservableProperty]
    private int sunsetOffset;

    [ObservableProperty]
    [property: JsonIgnore]
    private bool sunsetOffsetIsBefore;

    [ObservableProperty]
    [property: JsonIgnore]
    private int sunsetOffsetMinutes;

    [ObservableProperty]
    private TimeSpan? fixedOnTime;

    [ObservableProperty]
    private TimeSpan? fixedOffTime;

    [ObservableProperty]
    private LedColor? targetColor;

    [JsonIgnore]
    public bool HasSunriseOnEvent => SunriseEnabled && SunriseTurnsOn;

    [JsonIgnore]
    public bool HasSunriseOffEvent => SunriseEnabled && !SunriseTurnsOn;

    [JsonIgnore]
    public bool HasSunsetOnEvent => SunsetEnabled && SunsetTurnsOn;

    [JsonIgnore]
    public bool HasSunsetOffEvent => SunsetEnabled && !SunsetTurnsOn;

    [JsonIgnore]
    public string FixedOnTimeText
    {
        get => FormatTime(FixedOnTime);
        set => UpdateTimeFromText(value, isOnTime: true);
    }

    [JsonIgnore]
    public string FixedOffTimeText
    {
        get => FormatTime(FixedOffTime);
        set => UpdateTimeFromText(value, isOnTime: false);
    }

    partial void OnSunriseEnabledChanged(bool value)
    {
        NotifySunriseEventFlagsChanged();
    }

    partial void OnSunriseTurnsOnChanged(bool value)
    {
        NotifySunriseEventFlagsChanged();
    }

    partial void OnFixedOnTimeChanged(TimeSpan? value)
    {
        OnPropertyChanged(nameof(FixedOnTimeText));
    }

    partial void OnFixedOffTimeChanged(TimeSpan? value)
    {
        OnPropertyChanged(nameof(FixedOffTimeText));
    }

    partial void OnSunsetEnabledChanged(bool value)
    {
        NotifySunsetEventFlagsChanged();
    }

    partial void OnSunsetTurnsOnChanged(bool value)
    {
        NotifySunsetEventFlagsChanged();
    }

    partial void OnSunriseOffsetChanged(int value)
    {
        if (_syncingSunriseOffset)
        {
            return;
        }

        _syncingSunriseOffset = true;
        SunriseOffsetIsBefore = value < 0;
        SunriseOffsetMinutes = Math.Abs(value);
        _syncingSunriseOffset = false;
    }

    partial void OnSunriseOffsetIsBeforeChanged(bool value)
    {
        if (_syncingSunriseOffset)
        {
            return;
        }

        _syncingSunriseOffset = true;
        var minutes = Math.Abs(SunriseOffsetMinutes);
        SunriseOffset = value ? -minutes : minutes;
        _syncingSunriseOffset = false;
    }

    partial void OnSunriseOffsetMinutesChanged(int value)
    {
        if (_syncingSunriseOffset)
        {
            return;
        }

        _syncingSunriseOffset = true;
        var minutes = Math.Max(0, value);
        SunriseOffset = SunriseOffsetIsBefore ? -minutes : minutes;
        _syncingSunriseOffset = false;
    }

    partial void OnSunsetOffsetChanged(int value)
    {
        if (_syncingSunsetOffset)
        {
            return;
        }

        _syncingSunsetOffset = true;
        SunsetOffsetIsBefore = value < 0;
        SunsetOffsetMinutes = Math.Abs(value);
        _syncingSunsetOffset = false;
    }

    partial void OnSunsetOffsetIsBeforeChanged(bool value)
    {
        if (_syncingSunsetOffset)
        {
            return;
        }

        _syncingSunsetOffset = true;
        var minutes = Math.Abs(SunsetOffsetMinutes);
        SunsetOffset = value ? -minutes : minutes;
        _syncingSunsetOffset = false;
    }

    partial void OnSunsetOffsetMinutesChanged(int value)
    {
        if (_syncingSunsetOffset)
        {
            return;
        }

        _syncingSunsetOffset = true;
        var minutes = Math.Max(0, value);
        SunsetOffset = SunsetOffsetIsBefore ? -minutes : minutes;
        _syncingSunsetOffset = false;
    }

    private void NotifySunriseEventFlagsChanged()
    {
        OnPropertyChanged(nameof(HasSunriseOnEvent));
        OnPropertyChanged(nameof(HasSunriseOffEvent));
    }

    private void NotifySunsetEventFlagsChanged()
    {
        OnPropertyChanged(nameof(HasSunsetOnEvent));
        OnPropertyChanged(nameof(HasSunsetOffEvent));
    }

    private void UpdateTimeFromText(string? value, bool isOnTime)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (isOnTime)
            {
                FixedOnTime = null;
            }
            else
            {
                FixedOffTime = null;
            }

            return;
        }

        var trimmed = value.Trim();
        if (!TryParseTime(trimmed, out var parsed))
        {
            OnPropertyChanged(isOnTime ? nameof(FixedOnTimeText) : nameof(FixedOffTimeText));
            return;
        }

        if (isOnTime)
        {
            FixedOnTime = parsed;
        }
        else
        {
            FixedOffTime = parsed;
        }
    }

    private static bool TryParseTime(string value, out TimeSpan parsed)
    {
        if (TimeSpan.TryParseExact(value, "hh\\:mm", CultureInfo.InvariantCulture, out parsed))
        {
            return true;
        }

        if (TimeSpan.TryParseExact(value, "h\\:mm", CultureInfo.InvariantCulture, out parsed))
        {
            return true;
        }

        return false;
    }

    private static string FormatTime(TimeSpan? value)
    {
        return value?.ToString("hh\\:mm", CultureInfo.InvariantCulture) ?? string.Empty;
    }
}
