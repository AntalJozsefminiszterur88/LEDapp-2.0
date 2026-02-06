using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace LedController.UI.Converters;

public sealed class HexToForegroundBrushConverter : IValueConverter
{
    public IBrush? LightBrush { get; set; }
    public IBrush? DarkBrush { get; set; }
    public double Threshold { get; set; } = 0.6;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var color = TryResolveColor(value);
        if (color is null)
        {
            return LightBrush ?? Brushes.White;
        }

        var luminance = (0.2126 * color.Value.R + 0.7152 * color.Value.G + 0.0722 * color.Value.B) / 255.0;
        return luminance >= Threshold
            ? DarkBrush ?? Brushes.Black
            : LightBrush ?? Brushes.White;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return BindingOperations.DoNothing;
    }

    private static Color? TryResolveColor(object? value)
    {
        if (value is Color color)
        {
            return color;
        }

        if (value is ISolidColorBrush solid)
        {
            return solid.Color;
        }

        if (value is string text && Color.TryParse(text, out var parsed))
        {
            return parsed;
        }

        return null;
    }
}
