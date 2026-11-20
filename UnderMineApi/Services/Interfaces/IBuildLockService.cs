namespace UnderMineApi.Services.Interfaces;

/// <summary>
/// Cross-process lock for a build. A build may only be processed by the worker that holds its
/// lock; the lock is released automatically when the handle is disposed or the process dies.
/// </summary>
public interface IBuildLockService
{
    /// <summary>
    /// Tries to acquire the lock for <paramref name="buildId"/>. Returns null when another
    /// process already owns it.
    /// </summary>
    Task<IAsyncDisposable?> TryAcquireAsync(long buildId, CancellationToken ct = default);
}
