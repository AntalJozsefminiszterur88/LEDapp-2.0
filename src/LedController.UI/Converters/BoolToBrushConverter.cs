using System;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace LedController.UI.Converters;

public sealed class BoolToBrushConverter : IValueConverter
{
    public IBrush? TrueBrush { get; set; }
    public IBrush? FalseBrush { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        var flag = value is bool b && b;
        return flag ? TrueBrush ?? Brushes.LimeGreen : FalseBrush ?? Brushes.IndianRed;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        return BindingOperations.DoNothing;
    }
}
