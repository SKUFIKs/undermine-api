using API_UnderMine.Services;
using API_UnderMine_v0_1.Data;
using Microsoft.EntityFrameworkCore;

namespace API_UnderMine.Services;

public class ConfigUpdateService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ConfigUpdateService> _logger;
    private readonly ConfigCacheService _cache;
    private readonly TimeSpan _updateInterval = TimeSpan.FromMinutes(30);

    public ConfigUpdateService(IServiceProvider serviceProvider, ConfigCacheService cache, ILogger<ConfigUpdateService> logger)
    {
        _serviceProvider = serviceProvider;
        _cache = cache;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Updating config from database...");

                using (var scope = _serviceProvider.CreateScope())
                {
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                    var config = await dbContext.CoreDatas.AsNoTracking().FirstOrDefaultAsync(stoppingToken);

                    if (config != null)
                    {
                        _cache.CachedConfig = config;
                        _logger.LogInformation("Config updated successfully.");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating config from DB");
            }

            await Task.Delay(_updateInterval, stoppingToken);
        }
    }
}