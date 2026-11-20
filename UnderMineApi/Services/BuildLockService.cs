using Npgsql;
using UnderMineApi.Services.Interfaces;

namespace UnderMineApi.Services;

/// <summary>
/// PostgreSQL session-level advisory lock keyed by the build's numeric id.
///
/// The dedicated lock connection is intentionally created with <c>Pooling=false</c>: a session
/// advisory lock belongs to a physical connection, and returning that connection to a pool could
/// leak the lock to an unrelated request. Closing the connection (or a process crash) releases
/// the lock automatically.
/// </summary>
public sealed class BuildLockService : IBuildLockService
{
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);

    private readonly string _connectionString;
    private readonly ILogger<BuildLockService> _logger;

    public BuildLockService(IConfiguration configuration, ILogger<BuildLockService> logger)
    {
        _logger = logger;

        var raw = configuration.GetConnectionString("PostgreConnection")
                  ?? throw new InvalidOperationException(
                      "Connection string 'PostgreConnection' is missing; the build lock cannot work.");

        var builder = new NpgsqlConnectionStringBuilder(raw)
        {
            Pooling = false,
            ApplicationName = "UnderMine-BuildLock"
        };

        _connectionString = builder.ConnectionString;
    }

    public async Task<IAsyncDisposable?> TryAcquireAsync(long buildId, CancellationToken ct = default)
    {
        var connection = new NpgsqlConnection(_connectionString);

        try
        {
            await connection.OpenAsync(ct);

            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@buildId)", connection);
            command.Parameters.AddWithValue("buildId", buildId);

            var acquired = await command.ExecuteScalarAsync(ct);
            if (acquired is true)
            {
                _logger.LogDebug("Acquired build lock for build {BuildId}", buildId);
                return new LockHandle(connection, buildId, KeepAliveInterval, _logger);
            }
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        await connection.DisposeAsync();
        return null;
    }

    private sealed class LockHandle : IAsyncDisposable
    {
        private readonly NpgsqlConnection _connection;
        private readonly long _buildId;
        private readonly CancellationTokenSource _keepAliveCts = new();
        private readonly Task _keepAliveTask;
        private readonly ILogger _logger;
        private int _disposed;

        public LockHandle(
            NpgsqlConnection connection,
            long buildId,
            TimeSpan keepAliveInterval,
            ILogger logger)
        {
            _connection = connection;
            _buildId = buildId;
            _logger = logger;
            _keepAliveTask = KeepAliveAsync(connection, keepAliveInterval, _keepAliveCts.Token);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _keepAliveCts.Cancel();

            try
            {
                await _keepAliveTask;
            }
            catch (OperationCanceledException)
            {
                // expected on shutdown
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Build lock keep-alive stopped for build {BuildId}", _buildId);
            }
            finally
            {
                _keepAliveCts.Dispose();
                await _connection.DisposeAsync();
                _logger.LogDebug("Released build lock for build {BuildId}", _buildId);
            }
        }

        private static async Task KeepAliveAsync(
            NpgsqlConnection connection,
            TimeSpan interval,
            CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(interval, ct);

                    // Keep the session active so idle_session_timeout (if configured) does not
                    // silently drop the lock while the build is still running.
                    await using var command = new NpgsqlCommand("SELECT 1", connection);
                    await command.ExecuteScalarAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                // normal shutdown
            }
            catch
            {
                // The connection is gone; disposing the handle below releases whatever remains.
            }
        }
    }
}
