namespace UnderMineApi.Services.Interfaces;

/// <summary>Resolves and edits the permission set of a role.</summary>
public interface IPermissionService
{
    Task<IReadOnlySet<string>> GetPermissionsAsync(string role, CancellationToken cancellationToken = default);

    Task<bool> HasPermissionAsync(string role, string permission, CancellationToken cancellationToken = default);

    Task<bool> HasAnyPermissionAsync(
        string role,
        IEnumerable<string> permissions,
        CancellationToken cancellationToken = default);

    /// <summary>Replaces the permission set of a non-admin role.</summary>
    Task<(bool Success, string? Error)> SetRolePermissionsAsync(
        string role,
        IEnumerable<string> permissions,
        CancellationToken cancellationToken = default);

    void Invalidate();
}
