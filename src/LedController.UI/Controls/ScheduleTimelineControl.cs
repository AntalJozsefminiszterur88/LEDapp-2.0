using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using LedController.Core.Models;
using LedController.Infrastructure.Services;

namespace LedController.UI.Controls;

public sealed class ScheduleTimelineControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<ScheduleProfile>?> ProfilesProperty =
        AvaloniaProperty.Register<ScheduleTimelineControl, IReadOnlyList<ScheduleProfile>?>(nameof(Profiles));

    public static readonly StyledProperty<GeoCoordinate?> CoordinatesProperty =
        AvaloniaProperty.Register<ScheduleTimelineControl, GeoCoordinate?>(nameof(Coordinates));

    private static readonly DayOfWeek[] WeekOrder =
    {
        DayOfWeek.Monday,
        DayOfWeek.Tuesday,
        DayOfWeek.Wednesday,
        DayOfWeek.Thursday,
        DayOfWeek.Friday,
        DayOfWeek.Saturday,
        DayOfWeek.Sunday
    };

    private static readonly CultureInfo HungarianCulture = CultureInfo.GetCultureInfo("hu-HU");
    private static readonly LocationService SunCalculator = new();

    private readonly Dictionary<DayOfWeek, List<ScheduleSegment>> _segments = new();
    private readonly Dictionary<string, IBrush> _brushCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<ScheduleProfile> _attachedProfiles = new();
    private readonly HashSet<DailySchedule> _attachedSchedules = new();
    private readonly Dictionary<ScheduleProfile, IReadOnlyList<DailySchedule>?> _profileSchedules = new();
    private readonly Dictionary<INotifyCollectionChanged, NotifyCollectionChangedEventHandler> _scheduleCollectionHandlers = new();
    private readonly DispatcherTimer _timer;

    private bool _dirty = true;
    private DateTime _cachedWeekStart = DateTime.MinValue;
    private GeoCoordinate? _cachedCoordinates;
    private INotifyCollectionChanged? _profilesCollection;

    public ScheduleTimelineControl()
    {
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(1)
        };
        _timer.Tick += (_, _) => InvalidateVisual();

        AttachedToVisualTree += (_, _) => _timer.Start();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }

    public IReadOnlyList<ScheduleProfile>? Profiles
    {
        get => GetValue(ProfilesProperty);
        set => SetValue(ProfilesProperty, value);
    }

    public GeoCoordinate? Coordinates
    {
        get => GetValue(CoordinatesProperty);
        set => SetValue(CoordinatesProperty, value);
    }

    static ScheduleTimelineControl()
    {
        AffectsRender<ScheduleTimelineControl>(ProfilesProperty, CoordinatesProperty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ProfilesProperty)
        {
            var oldProfiles = change.OldValue as IReadOnlyList<ScheduleProfile>;
            var newProfiles = change.NewValue as IReadOnlyList<ScheduleProfile>;

            DetachProfiles(oldProfiles);
            AttachProfiles(newProfiles);

            if (_profilesCollection is not null)
            {
                _profilesCollection.CollectionChanged -= OnProfilesCollectionChanged;
            }

            _profilesCollection = newProfiles as INotifyCollectionChanged;
            if (_profilesCollection is not null)
            {
                _profilesCollection.CollectionChanged += OnProfilesCollectionChanged;
            }

            MarkDirty();
        }
        else if (change.Property == CoordinatesProperty)
        {
            MarkDirty();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        EnsureSegments(DateTime.Now);

        const double leftMargin = 72;
        const double topMargin = 8;
        const double bottomMargin = 24;
        const double rightMargin = 12;

        var dayCount = WeekOrder.Length;
        var rowHeight = Math.Max(18, (bounds.Height - topMargin - bottomMargin) / dayCount);
        var gridHeight = rowHeight * dayCount;
        var gridWidth = Math.Max(0, bounds.Width - leftMargin - rightMargin);

        var gridPen = new Pen(new SolidColorBrush(Color.Parse("#4b4b4b")), 1);
        var labelBrush = new SolidColorBrush(Color.Parse("#bdbdbd"));
        var nowPen = new Pen(new SolidColorBrush(Color.Parse("#ff4d4d")), 2);

        for (var hour = 0; hour <= 24; hour++)
        {
            var x = leftMargin + gridWidth * hour / 24.0;
            context.DrawLine(gridPen, new Point(x, topMargin), new Point(x, topMargin + gridHeight));

            if (hour % 2 == 0)
            {
                DrawText(context, labelBrush, $"{hour:00}", new Point(x - 10, topMargin + gridHeight + 4), 11);
            }
        }

        for (var i = 0; i < dayCount; i++)
        {
            var day = WeekOrder[i];
            var y = topMargin + i * rowHeight;

            DrawText(context, labelBrush, GetDayLabel(day), new Point(6, y + rowHeight * 0.2), 12);
            context.DrawLine(gridPen, new Point(leftMargin, y), new Point(leftMargin + gridWidth, y));

            if (_segments.TryGetValue(day, out var segments))
            {
                foreach (var segment in segments)
                {
                    if (segment.EndMinutes <= segment.StartMinutes)
                    {
                        continue;
                    }

                    var x1 = leftMargin + gridWidth * segment.StartMinutes / (24.0 * 60.0);
                    var x2 = leftMargin + gridWidth * segment.EndMinutes / (24.0 * 60.0);
                    var rect = new Rect(x1, y + 2, Math.Max(1, x2 - x1), rowHeight - 4);
                    context.FillRectangle(segment.Brush, rect);
                }
            }
        }

        context.DrawLine(gridPen,
            new Point(leftMargin, topMargin + gridHeight),
            new Point(leftMargin + gridWidth, topMargin + gridHeight));

        var now = DateTime.Now;
        var totalMinutes = now.Hour * 60 + now.Minute + now.Second / 60.0;
        var nowX = leftMargin + gridWidth * totalMinutes / (24.0 * 60.0);
        context.DrawLine(nowPen, new Point(nowX, topMargin), new Point(nowX, topMargin + gridHeight));
    }

    private void OnProfilesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (var item in e.OldItems.OfType<ScheduleProfile>())
            {
                DetachProfile(item);
            }
        }

        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems.OfType<ScheduleProfile>())
            {
                AttachProfile(item);
            }
        }

        MarkDirty();
    }

    private void AttachProfiles(IReadOnlyList<ScheduleProfile>? profiles)
    {
        if (profiles is null)
        {
            return;
        }

        foreach (var profile in profiles)
        {
            AttachProfile(profile);
        }
    }

    private void DetachProfiles(IReadOnlyList<ScheduleProfile>? profiles)
    {
        if (profiles is null)
        {
            return;
        }

        foreach (var profile in profiles)
        {
            DetachProfile(profile);
        }
    }

    private void AttachProfile(ScheduleProfile profile)
    {
        if (_attachedProfiles.Add(profile))
        {
            profile.PropertyChanged += OnProfilePropertyChanged;
            AttachScheduleCollection(profile, profile.DailySchedules);
        }
    }

    private void DetachProfile(ScheduleProfile profile)
    {
        if (_attachedProfiles.Remove(profile))
        {
            DetachScheduleCollection(profile);
            profile.PropertyChanged -= OnProfilePropertyChanged;
        }
    }

    private void OnProfilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ScheduleProfile profile)
        {
            return;
        }

        if (e.PropertyName == nameof(ScheduleProfile.DailySchedules))
        {
            DetachScheduleCollection(profile);
            AttachScheduleCollection(profile, profile.DailySchedules);
        }

        MarkDirty();
    }

    private void AttachScheduleCollection(ScheduleProfile profile, IReadOnlyList<DailySchedule>? schedules)
    {
        if (schedules is null)
        {
            _profileSchedules[profile] = null;
            return;
        }

        _profileSchedules[profile] = schedules;

        if (schedules is INotifyCollectionChanged notify && !_scheduleCollectionHandlers.ContainsKey(notify))
        {
            NotifyCollectionChangedEventHandler handler = (_, e) => OnScheduleCollectionChanged(e);
            _scheduleCollectionHandlers[notify] = handler;
            notify.CollectionChanged += handler;
        }

        AttachSchedules(schedules);
    }

    private void DetachScheduleCollection(ScheduleProfile profile)
    {
        if (!_profileSchedules.TryGetValue(profile, out var schedules) || schedules is null)
        {
            _profileSchedules.Remove(profile);
            return;
        }

        DetachSchedules(schedules);

        if (schedules is INotifyCollectionChanged notify &&
            _scheduleCollectionHandlers.TryGetValue(notify, out var handler))
        {
            notify.CollectionChanged -= handler;
            _scheduleCollectionHandlers.Remove(notify);
        }

        _profileSchedules.Remove(profile);
    }

    private void OnScheduleCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (var item in e.OldItems.OfType<DailySchedule>())
            {
                DetachSchedule(item);
            }
        }

        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems.OfType<DailySchedule>())
            {
                AttachSchedule(item);
            }
        }

        MarkDirty();
    }

    private void AttachSchedules(IReadOnlyList<DailySchedule>? schedules)
    {
        if (schedules is null)
        {
            return;
        }

        foreach (var schedule in schedules)
        {
            AttachSchedule(schedule);
        }
    }

    private void DetachSchedules(IReadOnlyList<DailySchedule>? schedules)
    {
        if (schedules is null)
        {
            return;
        }

        foreach (var schedule in schedules)
        {
            DetachSchedule(schedule);
        }
    }

    private void AttachSchedule(DailySchedule schedule)
    {
        if (_attachedSchedules.Add(schedule))
        {
            schedule.PropertyChanged += OnSchedulePropertyChanged;
        }
    }

    private void DetachSchedule(DailySchedule schedule)
    {
        if (_attachedSchedules.Remove(schedule))
        {
            schedule.PropertyChanged -= OnSchedulePropertyChanged;
        }
    }

    private void OnSchedulePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        MarkDirty();
    }

    private void MarkDirty()
    {
        _dirty = true;
        InvalidateVisual();
    }

    private void EnsureSegments(DateTime now)
    {
        var weekStart = GetWeekStart(now);
        var coords = Coordinates;
        if (coords is null || (coords.Latitude == 0 && coords.Longitude == 0))
        {
            var fallback = AppSettings.Default;
            coords = new GeoCoordinate(fallback.Latitude, fallback.Longitude);
        }

        if (!_dirty && _cachedWeekStart == weekStart && Equals(_cachedCoordinates, coords))
        {
            return;
        }

        _cachedWeekStart = weekStart;
        _cachedCoordinates = coords;
        _dirty = false;
        _segments.Clear();

        var profiles = Profiles;
        if (profiles is null || profiles.Count == 0)
        {
            return;
        }

        var datesByDay = new Dictionary<DayOfWeek, DateTime>();
        var sunTimesByDay = new Dictionary<DayOfWeek, SunTimes>();
        foreach (var day in WeekOrder)
        {
            var date = weekStart.AddDays(GetDayOffset(day));
            datesByDay[day] = date;
            sunTimesByDay[day] = SunCalculator.GetSunTimes(coords.Latitude, coords.Longitude, date);
        }

        foreach (var profile in profiles)
        {
            foreach (var day in WeekOrder)
            {
                var schedule = profile.GetSchedule(day);
                if (schedule is null)
                {
                    continue;
                }

                var date = datesByDay[day];
                var sunTimes = sunTimesByDay[day];

                if (!TryBuildInterval(schedule, date, sunTimes, out var start, out var end, out var brush))
                {
                    continue;
                }

                if (!_segments.TryGetValue(day, out var segments))
                {
                    segments = new List<ScheduleSegment>();
                    _segments[day] = segments;
                }

                var startMinutes = (int)Math.Floor(start.TimeOfDay.TotalMinutes);
                var endMinutes = (int)Math.Floor(end.TimeOfDay.TotalMinutes);

                if (end.Date > start.Date)
                {
                    segments.Add(new ScheduleSegment(startMinutes, 24 * 60, brush));
                    segments.Add(new ScheduleSegment(0, endMinutes, brush));
                }
                else
                {
                    segments.Add(new ScheduleSegment(startMinutes, endMinutes, brush));
                }
            }
        }
    }

    private bool TryBuildInterval(
        DailySchedule schedule,
        DateTime date,
        SunTimes sunTimes,
        out DateTime start,
        out DateTime end,
        out IBrush brush)
    {
        start = default;
        end = default;
        brush = Brushes.Transparent;

        if (schedule.TargetColor is null)
        {
            return false;
        }

        brush = ResolveBrush(schedule.TargetColor);

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

        if (onEvents.Count == 0 || offEvents.Count == 0)
        {
            return false;
        }

        start = onEvents.Min();

        DateTime? candidate = null;
        foreach (var off in offEvents)
        {
            var adjusted = off <= start ? off.AddDays(1) : off;
            if (candidate is null || adjusted < candidate.Value)
            {
                candidate = adjusted;
            }
        }

        if (candidate is null)
        {
            return false;
        }

        end = candidate.Value;

        if (end <= start)
        {
            end = end.AddDays(1);
        }

        return true;
    }

    private IBrush ResolveBrush(LedColor color)
    {
        var hex = color.Hex;
        if (string.IsNullOrWhiteSpace(hex))
        {
            hex = color.NormalizedHex;
        }

        if (string.IsNullOrWhiteSpace(hex))
        {
            return Brushes.White;
        }

        if (_brushCache.TryGetValue(hex, out var brush))
        {
            return brush;
        }

        if (!Color.TryParse(hex, out var parsed))
        {
            parsed = Colors.White;
        }

        brush = new SolidColorBrush(parsed);
        _brushCache[hex] = brush;
        return brush;
    }

    private static DateTime GetWeekStart(DateTime date)
    {
        var diff = ((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return date.Date.AddDays(-diff);
    }

    private static int GetDayOffset(DayOfWeek day)
    {
        return ((int)day - (int)DayOfWeek.Monday + 7) % 7;
    }

    private static string GetDayLabel(DayOfWeek day)
    {
        return HungarianCulture.DateTimeFormat.GetDayName(day);
    }

    private void DrawText(DrawingContext context, IBrush brush, string text, Point origin, double fontSize)
    {
        var typeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Normal);
        var formatted = new FormattedText(
            text,
            HungarianCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            brush);

        context.DrawText(formatted, origin);
    }

    private readonly struct ScheduleSegment
    {
        public ScheduleSegment(int startMinutes, int endMinutes, IBrush brush)
        {
            StartMinutes = startMinutes;
            EndMinutes = endMinutes;
            Brush = brush;
        }

        public int StartMinutes { get; }
        public int EndMinutes { get; }
        public IBrush Brush { get; }
    }
}
