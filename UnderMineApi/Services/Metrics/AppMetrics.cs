using Prometheus;
using PromMetrics = Prometheus.Metrics;

namespace UnderMineApi.Metrics;

/// <summary>
/// Central Prometheus metric definitions. Custom counters cover business events; snapshot
/// gauges are refreshed from PostgreSQL by <see cref="MetricsSnapshotService"/>.
/// </summary>
public static class AppMetrics
{
    private static readonly Counter LoginCounter = PromMetrics.CreateCounter(
        "undermine_auth_login_total",
        "Login attempts grouped by result.",
        new[] { "result" });

    private static readonly Counter RegistrationCounter = PromMetrics.CreateCounter(
        "undermine_auth_registration_total",
        "Registration attempts grouped by result.",
        new[] { "result" });

    private static readonly Counter TokenValidationCounter = PromMetrics.CreateCounter(
        "undermine_auth_token_validation_total",
        "Token validation attempts grouped by result.",
        new[] { "result" });

    private static readonly Counter BanCounter = PromMetrics.CreateCounter(
        "undermine_user_bans_total",
        "Ban and unban operations.",
        new[] { "action" });

    private static readonly Counter BanEvidenceFilesCounter = PromMetrics.CreateCounter(
        "undermine_ban_evidence_uploaded_total",
        "Ban evidence files stored in PostgreSQL.");

    private static readonly Counter BanEvidenceBytesCounter = PromMetrics.CreateCounter(
        "undermine_ban_evidence_uploaded_bytes_total",
        "Total size of ban evidence stored in PostgreSQL.");

    private static readonly Counter AdminActionCounter = PromMetrics.CreateCounter(
        "undermine_admin_actions_total",
        "Audited administrative actions grouped by action code.",
        new[] { "action" });

    private static readonly Counter BuildEventCounter = PromMetrics.CreateCounter(
        "undermine_build_events_total",
        "Build lifecycle events grouped by event and build type.",
        new[] { "event", "type" });

    private static readonly Histogram BuildDuration = PromMetrics.CreateHistogram(
        "undermine_build_duration_seconds",
        "Time from build creation until completion or failure.",
        new[] { "type", "result" },
        new HistogramConfiguration
        {
            Buckets = Histogram.ExponentialBuckets(30, 2, 10)
        });

    private static readonly Histogram BuildFiles = PromMetrics.CreateHistogram(
        "undermine_build_files",
        "Number of files in finished builds.",
        new[] { "type" },
        new HistogramConfiguration
        {
            Buckets = new[] { 0d, 10, 50, 100, 250, 500, 1000, 2000, 5000, 10000 }
        });

    private static readonly Histogram BuildBytes = PromMetrics.CreateHistogram(
        "undermine_build_size_bytes",
        "Total size of finished builds.",
        new[] { "type" },
        new HistogramConfiguration
        {
            Buckets = Histogram.ExponentialBuckets(100 * 1024 * 1024, 2, 8)
        });

    private static readonly Gauge UsersGauge = PromMetrics.CreateGauge(
        "undermine_users",
        "Current number of accounts by role.",
        new[] { "role" });

    private static readonly Gauge BannedUsersGauge = PromMetrics.CreateGauge(
        "undermine_users_banned",
        "Number of distinct accounts with an active, non-expired ban.");

    private static readonly Gauge ActiveUsers24hGauge = PromMetrics.CreateGauge(
        "undermine_users_active_24h",
        "Accounts with a successful login in the last 24 hours.");

    private static readonly Gauge Registrations24hGauge = PromMetrics.CreateGauge(
        "undermine_registrations_24h",
        "Accounts registered in the last 24 hours.");

    private static readonly Gauge ServersGauge = PromMetrics.CreateGauge(
        "undermine_servers",
        "Current number of servers by state.",
        new[] { "status" });

    private static readonly Gauge BuildsGauge = PromMetrics.CreateGauge(
        "undermine_builds",
        "Current number of builds by status.",
        new[] { "status" });

    private static readonly Gauge BuildQueueGauge = PromMetrics.CreateGauge(
        "undermine_build_queue_length",
        "Builds waiting in pending status.");

    private static readonly Gauge BuildsInProgressGauge = PromMetrics.CreateGauge(
        "undermine_builds_in_progress",
        "Builds currently downloading or hashing.");

    private static readonly Gauge PresetsGauge = PromMetrics.CreateGauge(
        "undermine_presets",
        "Current number of build presets.");

    private static readonly Gauge BuildFilesGauge = PromMetrics.CreateGauge(
        "undermine_build_files_total",
        "Current number of stored build file rows.");

    private static readonly Gauge BuildBytesGauge = PromMetrics.CreateGauge(
        "undermine_build_bytes_total",
        "Current total size of stored build files.");

    private static readonly Gauge BanEvidenceFilesGauge = PromMetrics.CreateGauge(
        "undermine_ban_evidence_files",
        "Current number of ban evidence files.");

    private static readonly Gauge BanEvidenceBytesGauge = PromMetrics.CreateGauge(
        "undermine_ban_evidence_bytes",
        "Current total size of ban evidence files.");

    private static readonly Gauge AdminAuditRowsGauge = PromMetrics.CreateGauge(
        "undermine_admin_audit_rows",
        "Current number of audited administrative actions.");

    private static readonly Gauge RolePermissionsGauge = PromMetrics.CreateGauge(
        "undermine_role_permissions",
        "Configured permission count by role.",
        new[] { "role" });

    private static readonly Gauge DatabaseUpGauge = PromMetrics.CreateGauge(
        "undermine_database_up",
        "1 when the last PostgreSQL snapshot succeeded, 0 otherwise.");

    private static readonly Gauge MaintenanceGauge = PromMetrics.CreateGauge(
        "undermine_maintenance_mode",
        "1 when maintenance mode is enabled, 0 otherwise.");

    private static readonly Gauge StorageUpGauge = PromMetrics.CreateGauge(
        "undermine_storage_up",
        "1 when the last S3/MinIO health probe succeeded, 0 otherwise.");

    private static readonly Gauge RedisUpGauge = PromMetrics.CreateGauge(
        "undermine_redis_up",
        "1 when Redis is connected, 0 otherwise (0 when Redis is not configured).");

    private static readonly Gauge WorkerUpGauge = PromMetrics.CreateGauge(
        "undermine_build_worker_up",
        "1 while the background build worker is running.");

    private static readonly Gauge WorkerHeartbeatGauge = PromMetrics.CreateGauge(
        "undermine_build_worker_last_iteration_timestamp_seconds",
        "Unix timestamp of the last completed build worker loop.");

    public static void Login(string result) => LoginCounter.WithLabels(result).Inc();

    public static void Registration(string result) => RegistrationCounter.WithLabels(result).Inc();

    public static void TokenValidation(string result) => TokenValidationCounter.WithLabels(result).Inc();

    public static void Ban(string action) => BanCounter.WithLabels(action).Inc();

    public static void BanEvidence(int files, long bytes)
    {
        if (files > 0)
            BanEvidenceFilesCounter.Inc(files);
        if (bytes > 0)
            BanEvidenceBytesCounter.Inc(bytes);
    }

    public static void AdminAction(string action) => AdminActionCounter.WithLabels(action).Inc();

    public static void BuildEvent(string eventName, string type) =>
        BuildEventCounter.WithLabels(eventName, type).Inc();

    public static void BuildStarted(string type) => BuildEvent("started", type);

    public static void BuildFinished(
        string type,
        string result,
        TimeSpan duration,
        int? files,
        long? bytes)
    {
        BuildEvent(result, type);
        BuildDuration.WithLabels(type, result).Observe(Math.Max(0, duration.TotalSeconds));

        if (result == "completed")
        {
            if (files.HasValue && files.Value >= 0)
                BuildFiles.WithLabels(type).Observe(files.Value);
            if (bytes.HasValue && bytes.Value >= 0)
                BuildBytes.WithLabels(type).Observe(bytes.Value);
        }
    }

    public static void Retry(string type) => BuildEvent("retried", type);

    public static void SetBuildWorkerState(bool running)
    {
        WorkerUpGauge.Set(running ? 1 : 0);
        if (running)
            WorkerHeartbeatGauge.SetToCurrentTimeUtc();
    }

    public static void SetBuildWorkerHeartbeat() => WorkerHeartbeatGauge.SetToCurrentTimeUtc();

    public static void SetDatabaseUp(bool up) => DatabaseUpGauge.Set(up ? 1 : 0);

    public static void SetMaintenance(bool enabled) => MaintenanceGauge.Set(enabled ? 1 : 0);

    public static void SetStorageUp(bool up) => StorageUpGauge.Set(up ? 1 : 0);

    public static void SetRedisUp(bool up) => RedisUpGauge.Set(up ? 1 : 0);

    public static void SetUsers(IReadOnlyDictionary<string, int> counts)
    {
        foreach (var role in new[] { "admin", "moderator", "user" })
            UsersGauge.WithLabels(role).Set(counts.TryGetValue(role, out var count) ? count : 0);
    }

    public static void SetBannedUsers(int count) => BannedUsersGauge.Set(count);

    public static void SetActiveUsers24h(int count) => ActiveUsers24hGauge.Set(count);

    public static void SetRegistrations24h(int count) => Registrations24hGauge.Set(count);

    public static void SetServers(int active, int inactive)
    {
        ServersGauge.WithLabels("active").Set(active);
        ServersGauge.WithLabels("inactive").Set(inactive);
    }

    public static void SetBuilds(IReadOnlyDictionary<string, int> counts)
    {
        foreach (var status in new[] { "pending", "downloading", "hashing", "completed", "failed" })
            BuildsGauge.WithLabels(status).Set(counts.TryGetValue(status, out var count) ? count : 0);
    }

    public static void SetBuildQueue(int pending) => BuildQueueGauge.Set(pending);

    public static void SetBuildsInProgress(int inProgress) => BuildsInProgressGauge.Set(inProgress);

    public static void SetPresets(int count) => PresetsGauge.Set(count);

    public static void SetBuildFiles(long count) => BuildFilesGauge.Set(count);

    public static void SetBuildBytes(long bytes) => BuildBytesGauge.Set(bytes);

    public static void SetBanEvidenceFiles(long count) => BanEvidenceFilesGauge.Set(count);

    public static void SetBanEvidenceBytes(long bytes) => BanEvidenceBytesGauge.Set(bytes);

    public static void SetAdminAuditRows(long count) => AdminAuditRowsGauge.Set(count);

    public static void SetRolePermissions(string role, int count) =>
        RolePermissionsGauge.WithLabels(role).Set(count);
}
