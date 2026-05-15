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

    public void SetDeviceScheduleEnabled(Guid deviceId, bool enabled)
    {
    }
}
