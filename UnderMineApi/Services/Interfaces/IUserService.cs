using UnderMineApi.DTOs;
using UnderMineApi.Models;

namespace UnderMineApi.Services.Interfaces
{
    /// <summary>Result of a self-service password change; a fresh JWT is returned on success.</summary>
    public sealed record ChangeOwnPasswordResult(
        bool Success,
        string? Error,
        string? Message,
        string? Token,
        int ExpiresIn);

    public interface IUserService
    {
        Task<object> GetPagedUsersAsync(int page, int pageSize, CancellationToken cancellationToken = default);
        Task<IEnumerable<object>> SearchUsersAsync(string query, int limit = 10, CancellationToken cancellationToken = default);
        Task<(bool Success, string? Error)> BanUserAsync(long id, BanRequestDto request, string actor, CancellationToken cancellationToken = default);
        Task<(bool Success, string? Error)> UnbanUserAsync(long id, string actor, CancellationToken cancellationToken = default);
        Task<(bool Success, string? Error)> ChangePasswordAsync(long id, string newPassword, string actor, CancellationToken cancellationToken = default);
        Task<(bool Success, string? Error)> ChangeUserRoleAsync(long id, string newRole, string actor, CancellationToken cancellationToken = default);

        /// <summary>
        /// Permanently deletes an account, its stats and its ban history. Refuses to delete the
        /// caller, administrators and accounts that still have database references.
        /// </summary>
        Task<(bool Success, string? Error)> DeleteUserAsync(
            long id,
            string confirmUsername,
            string reason,
            long actorUserId,
            string actor,
            CancellationToken cancellationToken = default);

        /// <summary>Changes the caller's own password (requires the current one) and revokes old tokens.</summary>
        Task<ChangeOwnPasswordResult> ChangeOwnPasswordAsync(long userId, string currentPassword, string newPassword, CancellationToken cancellationToken = default);
    }
}