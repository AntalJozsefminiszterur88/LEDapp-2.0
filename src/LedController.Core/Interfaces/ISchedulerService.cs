using LedController.Core.Models;

namespace LedController.Core.Interfaces;

public interface ISchedulerService
{
    event Action<LedDevice>? DeviceStateChanged;
    void Start();
    void Stop();
    void MarkDeviceStateDirty(Guid deviceId);
    Task SetDeviceScheduleEnabledAsync(Guid deviceId, bool enabled);
    void SetManualColorOverride(Guid deviceId, LedColor color);
    void ClearManualColorOverride(Guid deviceId);
}
