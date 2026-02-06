using UnderMineApi.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace UnderMineApi.Controllers
{
    [ApiController]
    [ApiVersion("1.0")]
    [Route("api/v{version:apiVersion}/admin")] 
    [Authorize(Roles = "Admin")] 
    public class AdminController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly ILogger<AdminController> _logger;

        public AdminController(AppDbContext context, ILogger<AdminController> logger)
        {
            _context = context;
            _logger = logger;
        }


        [HttpGet("users")]
        public async Task<IActionResult> GetAllUsers()
        {
            var users = await _context.McAuthAccounts
                .AsNoTracking() 
                .Select(u => new
                {
                    u.Id,
                    u.RealName,
                    u.Role,
                    u.RegIp,
                    u.LastLogin,
                    u.IsBanned,
                    u.DeviceId
                })
                .ToListAsync();

            return Ok(users);
        }

        [HttpPost("ban/{id}")]
        public async Task<IActionResult> BanUser(long id) 
        {
            var targetUser = await _context.McAuthAccounts.FindAsync(id);

            if (targetUser == null)
                return NotFound("Пользователь не найден");

            if (targetUser.Role == "Admin")
                return BadRequest("Нельзя забанить администратора!");

            targetUser.IsBanned = true;

            targetUser.HasSession = 0;

            await _context.SaveChangesAsync();

            _logger.LogWarning($"Admin banned user: {targetUser.RealName}");
            return Ok(new { message = $"Пользователь {targetUser.RealName} забанен" });
        }

        [HttpPost("promote/{id}")]
        public async Task<IActionResult> PromoteToAdmin(long id) 
        {
            var targetUser = await _context.McAuthAccounts.FindAsync(id);

            if (targetUser == null) return NotFound();

            targetUser.Role = "Admin";
            await _context.SaveChangesAsync();

            return Ok(new { message = $"{targetUser.RealName} теперь Администратор" });
        }
    }
}
