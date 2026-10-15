using UnderMineApi.DTOs;
using UnderMineApi.Models;

namespace UnderMineApi.Services.Interfaces
{
    public interface IServerService
    {
        Task<(bool Success, ServerManifest? Server, string? Error)> CreateServerAsync(AdminServerUpsertDto dto, string actor, CancellationToken cancellationToken = default);
        Task<(bool Success, string? Error)> UpdateServerAsync(long id, AdminServerUpsertDto dto, string actor, CancellationToken cancellationToken = default);
        Task<(bool Success, string? Error)> DeleteServerAsync(long id, string actor, CancellationToken cancellationToken = default);
        Task<IEnumerable<AdminServerDto>> GetAllServersAdminAsync(CancellationToken cancellationToken = default);
        Task<AdminServerDto?> GetServerAdminAsync(long id, CancellationToken cancellationToken = default);
    }
}