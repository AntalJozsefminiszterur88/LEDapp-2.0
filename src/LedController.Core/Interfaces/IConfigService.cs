using LedController.Core.Models;

namespace LedController.Core.Interfaces;

public interface IConfigService
{
    Task<AppConfig> LoadConfigAsync();
    Task SaveConfigAsync(AppConfig config);
}
