using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace LedController.UI.Converters;

public sealed class DayOfWeekToHungarianConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DayOfWeek day)
        {
            return value;
        }

        return day switch
        {
            DayOfWeek.Monday => "Hétfő",
            DayOfWeek.Tuesday => "Kedd",
            DayOfWeek.Wednesday => "Szerda",
            DayOfWeek.Thursday => "Csütörtök",
            DayOfWeek.Friday => "Péntek",
            DayOfWeek.Saturday => "Szombat",
            DayOfWeek.Sunday => "Vasárnap",
            _ => day.ToString()
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value;
    }
}
