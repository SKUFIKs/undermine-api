namespace UnderMineApi.Services
{
    public class ApplicationStatusService
    {
        private readonly ConfigCacheService _cache;

        public ApplicationStatusService(ConfigCacheService cache)
        {
            _cache = cache;
        }

        public bool IsApiLocked()
        {
            if (_cache.IsMaintenance) return true;
            return false;
        }
    }
}