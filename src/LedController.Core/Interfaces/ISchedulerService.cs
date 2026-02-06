namespace LedController.Core.Interfaces;

public interface ISchedulerService
{
    void Start();
    void Stop();
    void MarkDeviceStateDirty(Guid deviceId);
    void SetDeviceScheduleEnabled(Guid deviceId, bool enabled);
}
