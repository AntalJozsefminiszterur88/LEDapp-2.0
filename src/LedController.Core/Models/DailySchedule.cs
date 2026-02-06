using System;
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

    partial void OnSunriseEnabledChanged(bool value)
    {
        NotifySunriseEventFlagsChanged();
    }

    partial void OnSunriseTurnsOnChanged(bool value)
    {
        NotifySunriseEventFlagsChanged();
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
}
