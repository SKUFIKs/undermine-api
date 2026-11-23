namespace UnderMineApi.Services.Security;

/// <summary>UI-facing description of one configurable permission.</summary>
public sealed record PermissionDescriptor(string Key, string Label, string Description);

/// <summary>Built-in application roles. Admin is implicit (all permissions).</summary>
public static class Roles
{
    public const string Admin = "admin";
    public const string Moderator = "moderator";
    public const string User = "user";

    /// <summary>Roles a user can be assigned through the admin panel.</summary>
    public static readonly IReadOnlyList<string> Assignable = new[] { Admin, Moderator, User };

    /// <summary>Roles whose permissions are stored and editable in the admin panel.</summary>
    public static readonly IReadOnlyList<string> Configurable = new[] { Moderator, User };
}

/// <summary>
/// Fine-grained permissions used by the admin area. The admin role always has all of them;
/// other roles get the subset stored in <c>role_permissions</c>.
/// </summary>
public static class Permissions
{
    public const string ServersView = "servers.view";
    public const string ServersCreate = "servers.create";
    public const string ServersEdit = "servers.edit";
    public const string BuildsUpload = "builds.upload";
    public const string PresetsCreate = "presets.create";
    public const string UsersBan = "users.ban";
    public const string UsersChangePassword = "users.change_password";
    public const string UsersChangeRole = "users.change_role";
    public const string ActivityView = "activity.view";
    public const string ReportsView = "reports.view";
    public const string ReportsManage = "reports.manage";

    /// <summary>
    /// Pseudo-policy for "can open the Users page". A role needs at least one of the user
    /// management permissions; viewing the list without being able to act on it is pointless.
    /// </summary>
    public const string UsersViewPolicy = "users.view";

    /// <summary>Pseudo-policy for the admin-only settings page (maintenance + role access).</summary>
    public const string SettingsManagePolicy = "settings.manage";

    /// <summary>Policy that any admin-area permission satisfies (version catalogs, shared lookups).</summary>
    public const string AdminAreaPolicy = "admin.area";

    public static readonly IReadOnlyList<PermissionDescriptor> All = new[]
    {
        new PermissionDescriptor(ServersView, "View servers", "Open the admin server list and server details."),
        new PermissionDescriptor(ServersCreate, "Create servers", "Create new servers."),
        new PermissionDescriptor(ServersEdit, "Edit servers", "Edit and delete servers."),
        new PermissionDescriptor(BuildsUpload, "Upload builds", "Create, import, edit and delete builds."),
        new PermissionDescriptor(PresetsCreate, "Create presets", "Create and manage build presets and their mods."),
        new PermissionDescriptor(UsersBan, "Ban users", "View the user list, ban and unban regular users."),
        new PermissionDescriptor(UsersChangePassword, "Change user passwords", "Reset the password of a regular user."),
        new PermissionDescriptor(UsersChangeRole, "Change user roles", "Change roles between user and moderator."),
        new PermissionDescriptor(ActivityView, "View activity", "Read the administrative action log and its statistics."),
        new PermissionDescriptor(ReportsView, "View crash reports", "Read player crash reports and download their logs."),
        new PermissionDescriptor(ReportsManage, "Manage crash reports", "Change report status, leave admin notes and delete reports.")
    };

    public static readonly IReadOnlySet<string> AllKeys =
        All.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);

    public static readonly IReadOnlyList<string> UserManagement = new[]
    {
        UsersBan, UsersChangePassword, UsersChangeRole
    };

    /// <summary>Default permissions for the moderator role on a fresh database.</summary>
    public static IReadOnlyList<string> ModeratorDefaults { get; } = new[]
    {
        ServersView,
        ServersCreate,
        ServersEdit,
        BuildsUpload,
        PresetsCreate,
        UsersBan,
        ReportsView,
        ReportsManage
    };
}
