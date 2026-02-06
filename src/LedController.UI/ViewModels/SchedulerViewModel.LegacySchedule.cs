using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using LedController.Core.Models;

namespace LedController.UI.ViewModels;

public sealed partial class SchedulerViewModel
{
    private static readonly JsonSerializerOptions LegacyJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly DayOfWeek[] LegacyWeekOrder = new[]
    {
        DayOfWeek.Monday,
        DayOfWeek.Tuesday,
        DayOfWeek.Wednesday,
        DayOfWeek.Thursday,
        DayOfWeek.Friday,
        DayOfWeek.Saturday,
        DayOfWeek.Sunday
    };

    private static readonly Dictionary<DayOfWeek, string> LegacyDayNames = new()
    {
        [DayOfWeek.Monday] = "H\u00e9tf\u0151",
        [DayOfWeek.Tuesday] = "Kedd",
        [DayOfWeek.Wednesday] = "Szerda",
        [DayOfWeek.Thursday] = "Cs\u00fct\u00f6rt\u00f6k",
        [DayOfWeek.Friday] = "P\u00e9ntek",
        [DayOfWeek.Saturday] = "Szombat",
        [DayOfWeek.Sunday] = "Vas\u00e1rnap"
    };

    private static readonly Dictionary<string, DayOfWeek> LegacyDayLookup =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["H\u00e9tf\u0151"] = DayOfWeek.Monday,
            ["Kedd"] = DayOfWeek.Tuesday,
            ["Szerda"] = DayOfWeek.Wednesday,
            ["Cs\u00fct\u00f6rt\u00f6k"] = DayOfWeek.Thursday,
            ["P\u00e9ntek"] = DayOfWeek.Friday,
            ["Szombat"] = DayOfWeek.Saturday,
            ["Vas\u00e1rnap"] = DayOfWeek.Sunday,
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
            ["Z\u00f6ld"] = "#00ff00",
            ["K\u00e9k"] = "#0000ff",
            ["S\u00e1rga"] = "#ffff00",
            ["Ci\u00e1n"] = "#00ffff",
            ["Lila"] = "#800080",
            ["Narancs"] = "#ffa500",
            ["Feh\u00e9r"] = "#ffffff"
        };

    private string? _legacySchedulePath;

    public async Task ImportLegacyScheduleAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, Encoding.UTF8);
            var payload = JsonSerializer.Deserialize<Dictionary<string, LegacyScheduleProfile>>(json, LegacyJsonOptions);
            if (payload is null || payload.Count == 0)
            {
                return;
            }

            Profiles.Clear();
            foreach (var entry in payload)
            {
                var profile = BuildProfileFromLegacy(entry.Key, entry.Value);
                AlignColors(profile);
                Profiles.Add(profile);
            }

            SelectedProfile = Profiles.FirstOrDefault();
            _legacySchedulePath = path;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Scheduler] Legacy import failed: {ex.Message}");
        }
    }

    public async Task ExportLegacyScheduleAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            await WriteLegacyScheduleAsync(path);
            _legacySchedulePath = path;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Scheduler] Legacy export failed: {ex.Message}");
        }
    }

    private async Task WriteLegacyScheduleAsync(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var payload = BuildLegacyPayload();
        var json = JsonSerializer.Serialize(payload, LegacyJsonOptions);
        await File.WriteAllTextAsync(path, json, Encoding.UTF8);
    }

    private Dictionary<string, LegacyScheduleProfile> BuildLegacyPayload()
    {
        var result = new Dictionary<string, LegacyScheduleProfile>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in Profiles)
        {
            var legacyProfile = new LegacyScheduleProfile
            {
                Active = profile.IsActive
            };

            foreach (var day in LegacyWeekOrder)
            {
                var schedule = profile.GetSchedule(day);
                var legacyDay = new LegacyScheduleDay
                {
                    Color = schedule?.TargetColor?.Name ?? LedColor.Off.Name,
                    OnTime = schedule?.FixedOnTime?.ToString("hh\\:mm", CultureInfo.InvariantCulture) ?? string.Empty,
                    OffTime = schedule?.FixedOffTime?.ToString("hh\\:mm", CultureInfo.InvariantCulture) ?? string.Empty,
                    Sunrise = schedule?.SunriseEnabled ?? false,
                    SunriseOffset = schedule?.SunriseOffset ?? 0,
                    Sunset = schedule?.SunsetEnabled ?? false,
                    SunsetOffset = schedule?.SunsetOffset ?? 0
                };

                legacyProfile.Schedule[GetLegacyDayName(day)] = legacyDay;
            }

            var name = string.IsNullOrWhiteSpace(profile.Name) ? "Profil" : profile.Name;
            result[name] = legacyProfile;
        }

        return result;
    }

    private ScheduleProfile BuildProfileFromLegacy(string name, LegacyScheduleProfile legacy)
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
                schedule.TargetColor = AvailableColors.FirstOrDefault() ?? LedColor.Off;
            }

            profile.DailySchedules.Add(schedule);
        }

        return profile;
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

        return Enum.TryParse(key, true, out day);
    }

    private static string GetLegacyDayName(DayOfWeek day)
    {
        return LegacyDayNames.TryGetValue(day, out var name) ? name : day.ToString();
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

    private static string ResolveDefaultLegacyPath()
    {
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

    private LedColor ResolveLegacyColor(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return AvailableColors.FirstOrDefault() ?? LedColor.Off;
        }

        var match = AvailableColors.FirstOrDefault(c =>
            string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return match;
        }

        if (LegacyColorHex.TryGetValue(name.Trim(), out var hex))
        {
            var color = new LedColor(name.Trim(), hex);
            AvailableColors.Add(color);
            return color;
        }

        return AvailableColors.FirstOrDefault() ?? LedColor.Off;
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
}
