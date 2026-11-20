namespace UnderMineApi.Services.Interfaces;

/// <summary>Active ban state of an account (only the current/latest ban matters).</summary>
public sealed record BanStatus(bool IsBanned, string? Reason, long? ExpiresAt, string? BannedBy)
{
    public static readonly BanStatus NotBanned = new(false, null, null, null);
}

/// <summary>
/// Snapshot of everything an issued JWT is allowed to rely on: the account still exists, its
/// current role, token version and whether it is banned. Cached for a short time so a token is
/// not trusted forever after a ban/role/password change.
/// </summary>
public sealed record UserAccessSnapshot(bool Exists, string? Role, int TokenVersion, BanStatus Ban)
{
    public static readonly UserAccessSnapshot Missing = new(false, null, 0, BanStatus.NotBanned);
}

public interface IUserAccessService
{
    Task<UserAccessSnapshot> GetSnapshotAsync(long userId, CancellationToken ct = default);

    /// <summary>Drops the cached snapshot (call after ban/unban/role changes).</summary>
    void Invalidate(long userId);
}
