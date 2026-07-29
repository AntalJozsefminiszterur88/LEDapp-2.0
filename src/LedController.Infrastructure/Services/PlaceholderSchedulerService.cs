using LedController.Core.Interfaces;
using LedController.Core.Models;

namespace LedController.Infrastructure.Services;

public sealed class PlaceholderSchedulerService : ISchedulerService
{
    public event Action<LedDevice>? DeviceStateChanged
    {
        add { }
        remove { }
    }

    public void Start()
    {
    }

    public void Stop()
    {
    }

    public void MarkDeviceStateDirty(Guid deviceId)
    {
    }

    public Task SetDeviceScheduleEnabledAsync(Guid deviceId, bool enabled)
    {
        return Task.CompletedTask;
    }

    public void SetManualColorOverride(Guid deviceId, LedColor color)
    {
    }

    public void ClearManualColorOverride(Guid deviceId)
    {
    }
}
