using System.ComponentModel.DataAnnotations;

namespace UnderMineApi.DTOs
{
    public class BanRequestDto
    {
        public string? Reason { get; set; }
        public int? DurationDays { get; set; } // Если null, то бан навсегда
    }

    // Для метода смены пароля
    public class AdminChangePasswordDto
    {
        [Required]
        [MinLength(6)]
        public string NewPassword { get; set; } = null!;
    }
    public class AdminServerUpsertDto
    {
        [Required(AllowEmptyStrings = false)]
        [MaxLength(100)]
        public string ServerName { get; set; } = string.Empty;

        [Required(AllowEmptyStrings = false)]
        [MaxLength(50)]
        public string AppVersion { get; set; } = string.Empty;

        [Required(AllowEmptyStrings = false)]
        [MaxLength(50)]
        public string LoaderType { get; set; } = string.Empty;

        public string IpAddress { get; set; } = string.Empty;

        // Loader version - empty is legitimate (e.g. vanilla), only null must be avoided.
        public string? TypeVersion { get; set; } = string.Empty;
        public bool IsActive { get; set; }

        // Raw manifest JSON. Kept for backward compatibility; when set it wins over BuildId.
        public string? BuildManifest { get; set; }

        /// <summary>
        /// Which build to link to this server (its manifest is served to the launcher):
        ///   null (omitted) -> auto: the newest completed build matching MC / loader versions,
        ///   ""             -> none: detach the manifest,
        ///   "&lt;buildId&gt;"    -> pin to that build.
        /// </summary>
        public string? BuildId { get; set; }
    }
    public class AdminChangeRoleDto
    {
        public string Role { get; set; } = string.Empty;
    }

    /// <summary>
    /// Confirmation payload for irreversible account deletion. The caller must retype the
    /// target username and provide a reason; both are checked server-side and audited.
    /// </summary>
    public class DeleteUserRequestDto
    {
        [Required(AllowEmptyStrings = false)]
        [MaxLength(100)]
        public string ConfirmUsername { get; set; } = string.Empty;

        [Required(AllowEmptyStrings = false)]
        [MinLength(5)]
        [MaxLength(500)]
        public string Reason { get; set; } = string.Empty;
    }

    /// <summary>
    /// Aggregated address a user authenticated from. <see cref="SeenCount"/> includes the
    /// registration address and every successful login from that address.
    /// </summary>
    public class AdminUserIpDto
    {
        public string Ip { get; set; } = string.Empty;
        public long FirstSeen { get; set; }
        public long LastSeen { get; set; }
        public int SeenCount { get; set; }
    }

    /// <summary>Personal account statistics shown in the admin user-details dialog.</summary>
    public class AdminUserStatisticsDto
    {
        public long Id { get; set; }
        public string Username { get; set; } = string.Empty;
        public string RealName { get; set; } = string.Empty;
        public string Uuid { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public string? Email { get; set; }

        public long RegDate { get; set; }
        public long? LastLogin { get; set; }
        public string? RegIp { get; set; }
        public string? LastIp { get; set; }

        public long PlayTime { get; set; }
        public int Murders { get; set; }
        public int Deaths { get; set; }

        public bool IsBanned { get; set; }
        public string? BanReason { get; set; }
        public long? BanExpiresAt { get; set; }
        public string? BannedBy { get; set; }

        public List<AdminUserIpDto> IpHistory { get; set; } = new();
    }

    /// <summary>
    /// Server configuration links as exposed to the admin panel.
    /// "LoaderVersion" is the loader version (stored as TypeVersion in the database).
    /// </summary>
    public class AdminServerConfigDto
    {
        public string LoaderVersion { get; set; } = string.Empty;
    }

    /// <summary>
    /// Full server representation for the admin panel (includes inactive servers).
    /// BuildManifest is only populated by the single-server endpoint because it can be large.
    /// </summary>
    public class AdminServerDto
    {
        public long Id { get; set; }
        public string ServerName { get; set; } = string.Empty;
        public string AppVersion { get; set; } = string.Empty;
        public string LoaderType { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public string? BuildManifest { get; set; }
        public string? BuildId { get; set; }
        public AdminServerConfigDto? Config { get; set; }
    }
}
