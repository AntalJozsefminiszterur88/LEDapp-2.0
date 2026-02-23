namespace LedController.Core.Interfaces;

public interface IStartupService
{
    bool IsEnabled();
    bool SetEnabled(bool enabled);
}
