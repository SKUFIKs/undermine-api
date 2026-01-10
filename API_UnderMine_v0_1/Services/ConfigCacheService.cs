using API_UnderMine.Models;
using API_UnderMine_v0_1.Data;
using Microsoft.EntityFrameworkCore;

namespace API_UnderMine.Services
{
    public class ConfigCacheService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<ConfigCacheService> _logger;

        // Переименуй здесь: было CachedConfig -> стало CachedCore
        public CoreData? CachedCore { get; set; }
        public TechnoConfig? CachedTechno { get; set; }
        public VanilaConfig? CachedVanila { get; set; }

        public ConfigCacheService(IServiceScopeFactory scopeFactory, ILogger<ConfigCacheService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public async Task RefreshCacheAsync(CancellationToken ct = default)
        {
            try
            {
                using (var scope = _scopeFactory.CreateScope())
                {
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                    // Загружаем данные. Обрати внимание на присвоение в CachedCore
                    CachedCore = await dbContext.CoreDatas.AsNoTracking().FirstOrDefaultAsync(ct);
                    CachedTechno = await dbContext.TechnoConfigs.AsNoTracking().FirstOrDefaultAsync(ct);
                    CachedVanila = await dbContext.VanilaConfigs.AsNoTracking().FirstOrDefaultAsync(ct);

                    _logger.LogInformation("Кэш обновлен успешно.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка обновления кэша");
            }
        }
    }
}