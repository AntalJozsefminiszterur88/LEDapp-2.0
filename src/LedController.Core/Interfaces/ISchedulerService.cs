using LedController.Core.Models;

namespace LedController.Core.Interfaces;

public interface ISchedulerService
{
    event Action<LedDevice>? DeviceStateChanged;
    void Start();
    void Stop();
    void MarkDeviceStateDirty(Guid deviceId);
    void SetDeviceScheduleEnabled(Guid deviceId, bool enabled);
}
