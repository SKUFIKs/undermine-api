using Microsoft.EntityFrameworkCore;

namespace UnderMineApi.Data;

/// <summary>
/// Rewinds the identity (or serial) sequence of a table so deleted IDs become reusable.
/// </summary>
/// <remarks>
/// Postgres sequences only ever move forward: deleting the highest row of a table leaves the
/// sequence at its high-water mark, so the next insert continues from there and the IDs shown in
/// the admin panel keep growing forever (delete everything -&gt; next build is #37 instead of #1).
/// Rewinding to <c>MAX(id)</c> after a delete restores the natural numbering: an empty table
/// restarts at 1, otherwise the next insert continues right after the current maximum.
///
/// The operation is best-effort housekeeping - it must never fail the delete that preceded it,
/// so callers wrap it in try/catch.
/// </remarks>
public static class IdentitySequence
{
    // Allow-list of table names: they are interpolated into SQL (identifiers cannot be passed as
    // parameters), so they must only ever come from these constants, never from a request.
    public const string Builds = "build_instances";
    public const string BuildFiles = "build_files";
    public const string BuildLogs = "build_logs";
    public const string Presets = "build_presets";
    public const string Servers = "server_manifests";

    /// <summary>
    /// Sets the sequence of <paramref name="table"/> to the current MAX(id) (or 1 when the table
    /// is empty) so the next insert reuses the ID freed by the delete.
    /// </summary>
    public static Task RewindAsync(this AppDbContext context, string table, CancellationToken ct = default)
    {
        // setval(seq, value, is_called):
        //   empty table   -> setval(seq, 1, false) -> next nextval() returns 1
        //   rows present  -> setval(seq, max, true) -> next nextval() returns max + 1
        // CASE short-circuits, so setval is not called when the column has no sequence at all.
        var sql = $"""
            SELECT CASE
                WHEN pg_get_serial_sequence('{table}', 'id') IS NULL THEN NULL
                ELSE setval(
                    pg_get_serial_sequence('{table}', 'id'),
                    GREATEST(COALESCE((SELECT MAX(id) FROM {table}), 1), 1),
                    EXISTS (SELECT 1 FROM {table}))
            END
            """;

        return context.Database.ExecuteSqlRawAsync(sql, ct);
    }
}