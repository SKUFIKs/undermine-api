using Microsoft.AspNetCore.Mvc;

namespace API_UnderMine.Controllers
{
    [ApiController]
    [Route("api/debug")]
    public class DebugController : Controller
    {
        [HttpGet("ip")]
        public IActionResult GetIp()
        {
            var connection = HttpContext.Connection;
            var headers = HttpContext.Request.Headers;

            return Ok(new
            {
                // Какой IP видит ASP.NET после всех обработок
                ResolvedRemoteIp = connection.RemoteIpAddress?.ToString(),

                // Сырые заголовки (здесь мы ищем реальный IP)
                Headers = headers.ToDictionary(h => h.Key, h => h.Value.ToString())
            });
        }
    }
}
