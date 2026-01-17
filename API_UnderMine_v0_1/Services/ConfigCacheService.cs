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
        public bool IsMaintenance { get; private set; }
        public bool IsTechnoEnabled { get; private set; }
        public bool IsVanilaEnabled { get; private set; }

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


                    CachedCore = await dbContext.CoreDatas.AsNoTracking().FirstOrDefaultAsync(ct);
                    CachedTechno = await dbContext.TechnoConfigs.AsNoTracking().FirstOrDefaultAsync(ct);
                    CachedVanila = await dbContext.VanilaConfigs.AsNoTracking().FirstOrDefaultAsync(ct);

                    // 2. Загружаем глобальные настройки
                    var settings = await dbContext.GlobalSettings.AsNoTracking().ToListAsync(ct);

                    IsMaintenance = settings.FirstOrDefault(s => s.Key == "Maintenance")?.Value == "true";
                    IsTechnoEnabled = settings.FirstOrDefault(s => s.Key == "Techno_Enabled")?.Value != "false";
                    IsVanilaEnabled = settings.FirstOrDefault(s => s.Key == "Vanila_Enabled")?.Value != "false";

                    _logger.LogInformation("Кэш и статусы доступа обновлены.");
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "Ошибка обновления кэша"); }
        }
    }
}