using Microsoft.AspNetCore.Mvc;

namespace UnderMineApi.Controllers
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
                ResolvedRemoteIp = connection.RemoteIpAddress?.ToString(),
                Headers = headers.ToDictionary(h => h.Key, h => h.Value.ToString())
            });
        }
    }
}
