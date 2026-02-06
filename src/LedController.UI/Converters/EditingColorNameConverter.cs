using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using LedController.Core.Models;

namespace LedController.UI.Converters;

public sealed class EditingColorNameConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var item = values.Count > 0 ? values[0] as LedColor : null;
        var editing = values.Count > 1 ? values[1] as LedColor : null;
        var editName = values.Count > 2 ? values[2] as string : null;

        if (item is null)
        {
            return BindingOperations.DoNothing;
        }

        if (ReferenceEquals(item, editing) && !string.IsNullOrWhiteSpace(editName))
        {
            return editName;
        }

        return item.Name;
    }

    public object? ConvertBack(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        return BindingOperations.DoNothing;
    }
}
