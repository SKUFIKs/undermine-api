using API_UnderMine.Data;
using API_UnderMine_v0_1.Data;
using API_UnderMine_v0_1.DTOs;
using API_UnderMine_v0_1.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace API_UnderMine.Controllers
{
    [Route("api/check")]
    [ApiController]
    public class VersionСheck : ControllerBase
    {
        private readonly LauncherConfig _context;
        public VersionСheck (LauncherConfig launcherConfig)
        {
            _context = launcherConfig;
        }


        [HttpGet("LauncherVersion")]
        public async Task CheckActuallVersion(string version)
        {

        }
    }
}
