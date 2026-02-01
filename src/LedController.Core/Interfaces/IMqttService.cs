namespace LedController.Core.Interfaces;

public interface IMqttService
{
    bool IsRunning { get; }
    Task StartAsync();
    Task StopAsync();
}
