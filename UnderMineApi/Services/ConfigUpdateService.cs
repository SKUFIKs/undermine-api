using StackExchange.Redis;

namespace UnderMineApi.Services;

public class ConfigUpdateService : BackgroundService
{
    private readonly ILogger<ConfigUpdateService> _logger;
    private readonly ConfigCacheService _cache;
    private readonly StackExchange.Redis.IConnectionMultiplexer? _redis;
    private readonly TimeSpan _updateInterval = TimeSpan.FromMinutes(1);

    public ConfigUpdateService(ConfigCacheService cache, ILogger<ConfigUpdateService> logger, StackExchange.Redis.IConnectionMultiplexer? redis = null)
    {
        _cache = cache;
        _logger = logger;
        _redis = redis;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogDebug("Starting config cache refresh");

                await _cache.RefreshCacheAsync();

                // Publish update notification so other instances can reload cache
                try
                {
                    if (_redis != null)
                    {
                        var sub = _redis.GetSubscriber();
                        await sub.PublishAsync(
                            RedisChannel.Literal("ConfigCache:Updated"),
                            DateTimeOffset.UtcNow.ToString("o")
                        );
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to publish update to Redis");
                }

                _logger.LogDebug("Config cache refresh completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating configurations in background mode");
            }

            await Task.Delay(_updateInterval, stoppingToken);
        }
    }
}
