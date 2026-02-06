using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace LedController.UI.Converters;

public sealed class ConnectionStateToBrushConverter : IMultiValueConverter
{
    public IBrush ConnectedBrush { get; set; } = new SolidColorBrush(Color.Parse("#3DDC84"));
    public IBrush ConnectingBrush { get; set; } = new SolidColorBrush(Color.Parse("#F39C12"));
    public IBrush DisconnectedBrush { get; set; } = new SolidColorBrush(Color.Parse("#E85D5D"));
    public IBrush UnknownBrush { get; set; } = new SolidColorBrush(Color.Parse("#6E6E6E"));

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var isConnected = values.Count > 0 && values[0] is bool connected ? connected : (bool?)null;
        var isConnecting = values.Count > 1 && values[1] is bool connecting ? connecting : (bool?)null;

        if (isConnecting == true)
        {
            return ConnectingBrush;
        }

        if (isConnected == true)
        {
            return ConnectedBrush;
        }

        if (isConnected == false)
        {
            return DisconnectedBrush;
        }

        return UnknownBrush;
    }
}
