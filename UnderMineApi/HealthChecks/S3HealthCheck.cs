using Microsoft.Extensions.Diagnostics.HealthChecks;
using UnderMineApi.Services;

namespace UnderMineApi.HealthChecks;

/// <summary>
/// Verifies that the configured S3/MinIO endpoint is reachable with the configured credentials.
/// It lists buckets instead of checking one bucket, so a fresh installation (bucket not created
/// yet) is still considered healthy.
/// </summary>
public sealed class S3HealthCheck : IHealthCheck
{
    private readonly IServiceScopeFactory _scopeFactory;

    public S3HealthCheck(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<S3StorageService>();

        return await storage.IsAvailableAsync(cancellationToken)
            ? HealthCheckResult.Healthy("S3/MinIO is reachable")
            : HealthCheckResult.Unhealthy("S3/MinIO is not reachable");
    }
}
