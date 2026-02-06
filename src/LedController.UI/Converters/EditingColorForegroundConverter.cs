using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;
using LedController.Core.Models;

namespace LedController.UI.Converters;

public sealed class EditingColorForegroundConverter : IMultiValueConverter
{
    public IBrush? LightBrush { get; set; }
    public IBrush? DarkBrush { get; set; }
    public double Threshold { get; set; } = 0.6;

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var item = values.Count > 0 ? values[0] as LedColor : null;
        var editing = values.Count > 1 ? values[1] as LedColor : null;
        var editHex = values.Count > 2 ? values[2] as string : null;

        var hex = item?.Hex;
        if (ReferenceEquals(item, editing) && !string.IsNullOrWhiteSpace(editHex))
        {
            hex = editHex;
        }

        if (string.IsNullOrWhiteSpace(hex) || !Color.TryParse(hex, out var parsed))
        {
            return LightBrush ?? Brushes.White;
        }

        var luminance = (0.2126 * parsed.R + 0.7152 * parsed.G + 0.0722 * parsed.B) / 255.0;
        return luminance >= Threshold
            ? DarkBrush ?? Brushes.Black
            : LightBrush ?? Brushes.White;
    }

    public object? ConvertBack(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        return BindingOperations.DoNothing;
    }
}
