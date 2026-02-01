using LedController.Core.Interfaces;
using LedController.Core.Models;

namespace LedController.Infrastructure.Services;

public sealed class PlaceholderBleService : IBleService
{
    public Task<IReadOnlyList<LedDevice>> ScanForDevicesAsync()
    {
        IReadOnlyList<LedDevice> result = Array.Empty<LedDevice>();
        return Task.FromResult(result);
    }

    public Task ConnectAsync(LedDevice device)
    {
        if (device is not null)
        {
            device.IsConnected = true;
        }

        return Task.CompletedTask;
    }

    public Task DisconnectAsync(LedDevice device)
    {
        if (device is not null)
        {
            device.IsConnected = false;
        }

        return Task.CompletedTask;
    }

    public Task SendCommandAsync(LedDevice device, byte[] command)
    {
        return Task.CompletedTask;
    }
}
