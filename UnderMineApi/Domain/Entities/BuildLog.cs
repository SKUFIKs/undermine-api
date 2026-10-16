using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace UnderMineApi.Domain.Entities;

/// <summary>
/// One line of a build's processing log. Written by <c>BuildProgressReporter</c> while the
/// build worker runs and read incrementally through <c>GET /builds/{id}/logs</c>, which
/// always filters by build - hence the index on the foreign key.
/// </summary>
[Table("build_logs")]
public class BuildLog
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    /// <summary>Owning build. Rows are cascade-deleted with their build.</summary>
    [Required]
    [Column("build_instance_id")]
    public long BuildInstanceId { get; set; }

    /// <summary>Severity: <c>info</c>, <c>warning</c> or <c>error</c>.</summary>
    [Required]
    [Column("level")]
    [MaxLength(10)]
    public string Level { get; set; } = "info";

    [Required]
    [Column("message")]
    public string Message { get; set; } = string.Empty;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public virtual BuildInstance? BuildInstance { get; set; }
}