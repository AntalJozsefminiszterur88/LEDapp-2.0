using LedController.Core.Interfaces;
using LedController.Core.Models;

namespace LedController.Infrastructure.Services;

public sealed class PlaceholderConfigService : IConfigService
{
    public Task<AppConfig> LoadConfigAsync()
    {
        return Task.FromResult(AppConfig.Empty);
    }

    public Task SaveConfigAsync(AppConfig config)
    {
        return Task.CompletedTask;
    }
}
