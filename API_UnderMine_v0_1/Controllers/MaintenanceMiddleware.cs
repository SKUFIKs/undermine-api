using API_UnderMine.Services;

namespace API_UnderMine.Controllers
{
    public class MaintenanceMiddleware
    {
        private readonly RequestDelegate _next;

        public MaintenanceMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context, ConfigCacheService cache)
        {
            // Если включен глобальный режим тех. работ
            if (cache.IsMaintenance)
            {
                context.Response.StatusCode = 503;
                await context.Response.WriteAsJsonAsync(new
                {
                    Error = "MAINTENANCE",
                    Message = "Сервер на технических работах."
                });
                return;
            }
            await _next(context);
        }
    }
}
