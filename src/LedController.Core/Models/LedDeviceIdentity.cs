namespace LedController.Core.Models;

public static class LedDeviceIdentity
{
    public static string GetConnectionId(LedDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (!string.IsNullOrWhiteSpace(device.DeviceIdentifier))
        {
            return device.DeviceIdentifier;
        }

        return device.MacAddress ?? string.Empty;
    }

    public static string GetStableId(LedDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);

        var connectionId = GetConnectionId(device);
        if (!string.IsNullOrWhiteSpace(connectionId))
        {
            return connectionId;
        }

        return device.Id.ToString();
    }

    public static bool Matches(LedDevice left, LedDevice right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (left.Id != Guid.Empty && right.Id != Guid.Empty && left.Id == right.Id)
        {
            return true;
        }

        if (MatchesIdentifier(left.DeviceIdentifier, right.DeviceIdentifier))
        {
            return true;
        }

        return MatchesIdentifier(left.MacAddress, right.MacAddress);
    }

    public static bool Matches(LedDevice device, string candidate)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        if (MatchesIdentifier(device.DeviceIdentifier, candidate))
        {
            return true;
        }

        if (MatchesIdentifier(device.MacAddress, candidate))
        {
            return true;
        }

        return string.Equals(device.Id.ToString(), candidate, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesIdentifier(string? left, string? right)
    {
        return !string.IsNullOrWhiteSpace(left) &&
               !string.IsNullOrWhiteSpace(right) &&
               string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
