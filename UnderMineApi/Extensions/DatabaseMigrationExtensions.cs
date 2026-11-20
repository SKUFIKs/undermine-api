using Microsoft.EntityFrameworkCore;
using UnderMineApi.Data;
using UnderMineApi.Options;

namespace UnderMineApi.Extensions;

public static class DatabaseMigrationExtensions
{
    /// <summary>
    /// Applies EF Core migrations with a bounded retry loop (the database container may still be
    /// starting). If every attempt fails the exception is rethrown, so the process exits instead
    /// of serving traffic against an outdated/missing schema.
    /// </summary>
    public static async Task MigrateDatabaseWithRetryAsync(this WebApplication app, bool force = false)
    {
        var configuration = app.Configuration;
        var logger = app.Services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("DatabaseMigration");

        // API replicas (prod-2/prod-3) run with this disabled: migrations are applied once by
        // the primary/one-shot deployment step. The schema is expected to already exist.
        // --migrate-only forces the run, so the same image can be used as a migration job.
        if (!force && !RuntimeFlags.RunMigrations(configuration))
        {
            logger.LogInformation(
                "Database migrations are disabled (Database:MigrateOnStartup=false); skipping.");
            return;
        }

        var maxAttempts = Math.Max(1, configuration.GetValue("Database:MigrateRetries", 10));
        var delay = TimeSpan.FromSeconds(Math.Max(1, configuration.GetValue("Database:MigrateRetryDelaySeconds", 2)));

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var scope = app.Services.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                logger.LogInformation(
                    "Applying database migrations (attempt {Attempt}/{MaxAttempts})...", attempt, maxAttempts);

                await context.Database.MigrateAsync();

                logger.LogInformation("Database migrations applied successfully");
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                logger.LogWarning(
                    ex,
                    "Database migration attempt {Attempt}/{MaxAttempts} failed; retrying in {DelaySeconds}s",
                    attempt, maxAttempts, delay.TotalSeconds);

                await Task.Delay(delay);
            }
        }
    }
}
