using LedController.Core.Models;

namespace LedController.Core.Interfaces;

public interface IBleService
{
    Task<IReadOnlyList<LedDevice>> ScanForDevicesAsync();
    Task ConnectAsync(LedDevice device);
    Task DisconnectAsync(LedDevice device);
    Task SendCommandAsync(LedDevice device, byte[] command);
}
