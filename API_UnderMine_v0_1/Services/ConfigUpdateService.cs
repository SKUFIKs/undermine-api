using API_UnderMine.Services;

namespace API_UnderMine.Services;

public class ConfigUpdateService : BackgroundService
{
    private readonly ILogger<ConfigUpdateService> _logger;
    private readonly ConfigCacheService _cache;
    private readonly TimeSpan _updateInterval = TimeSpan.FromMinutes(15);

    public ConfigUpdateService(ConfigCacheService cache, ILogger<ConfigUpdateService> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Небольшая задержка перед первым запуском, чтобы БД успела инициализироваться
        await Task.Yield();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Запуск обновления кэша конфигураций из БД...");

                // Вызываем метод обновления, который мы написали в ConfigCacheService
                await _cache.RefreshCacheAsync();

                _logger.LogInformation("Все конфигурации (Core, Techno, Vanila) успешно обновлены.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при обновлении конфигураций в фоновом режиме");
            }

            // Ждем 15 минут до следующего обновления
            await Task.Delay(_updateInterval, stoppingToken);
        }
    }
}