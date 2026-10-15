using UnderMineApi.DTOs;
using UnderMineApi.Models;

namespace UnderMineApi.Services.Interfaces
{
    public interface IUserService
    {
        Task<object> GetPagedUsersAsync(int page, int pageSize, CancellationToken cancellationToken = default);
        Task<IEnumerable<object>> SearchUsersAsync(string query, int limit = 10, CancellationToken cancellationToken = default);
        Task<(bool Success, string? Error)> BanUserAsync(long id, BanRequestDto request, string actor, CancellationToken cancellationToken = default);
        Task<(bool Success, string? Error)> UnbanUserAsync(long id, string actor, CancellationToken cancellationToken = default);
        Task<(bool Success, string? Error)> ChangePasswordAsync(long id, string newPassword, string actor, CancellationToken cancellationToken = default);
        Task<(bool Success, string? Error)> ChangeUserRoleAsync(long id, string newRole, string actor, CancellationToken cancellationToken = default);
    }
}