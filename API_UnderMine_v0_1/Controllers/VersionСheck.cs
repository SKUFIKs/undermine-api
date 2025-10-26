using Microsoft.AspNetCore.Mvc;

namespace API_UnderMine.Controllers
{
    [Route("api/v0.1")]
    [ApiController]
    public class VersionCheck : ControllerBase
    {
        [HttpGet("LauncherVersion")]
        public IActionResult GetData()
        {
            var result = new
            {
                Version = "1.0.0",
                Required = true
            };

            return Ok(result);
        }
    }
}
