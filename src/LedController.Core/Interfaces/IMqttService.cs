namespace LedController.Core.Interfaces;

using LedController.Core.Models;

public interface IMqttService
{
    bool IsRunning { get; }
    Task StartAsync();
    Task StopAsync();
    Task PublishStateAsync(LedDevice device);
}
