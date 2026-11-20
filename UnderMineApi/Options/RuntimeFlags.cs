using Microsoft.Extensions.Configuration;

namespace UnderMineApi.Options;

/// <summary>
/// Reads the multi-node switches in one place so the defaults used by Program, migrations and
/// storage stay identical. Environment variables use the same names with double underscores:
/// <c>Build__WorkerEnabled</c>, <c>Database__MigrateOnStartup</c>, <c>S3__ManageBucket</c>.
/// </summary>
public static class RuntimeFlags
{
    /// <summary>Build queue worker. Default: enabled (primary node).</summary>
    public static bool RunBuildWorker(IConfiguration configuration) =>
        configuration.GetValue("Build:WorkerEnabled", true);

    /// <summary>Startup migrations. Default: enabled; replicas set this to false.</summary>
    public static bool RunMigrations(IConfiguration configuration) =>
        configuration.GetValue("Database:MigrateOnStartup", true);

    /// <summary>
    /// Bucket creation/policy management. Default: disabled — Garage provisions the bucket,
    /// key and website once from Ansible.
    /// </summary>
    public static bool ManageS3Bucket(IConfiguration configuration) =>
        configuration.GetValue("S3:ManageBucket", false);
}
