using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace LedController.UI.Converters;

public sealed class BoolToIndexConverter : IValueConverter
{
    public int FalseIndex { get; set; } = 0;
    public int TrueIndex { get; set; } = 1;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        return flag ? TrueIndex : FalseIndex;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int index)
        {
            return index == TrueIndex;
        }

        return BindingOperations.DoNothing;
    }
}
