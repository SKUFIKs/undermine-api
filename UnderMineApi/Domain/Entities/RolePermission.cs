using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace UnderMineApi.Domain.Entities
{
    /// <summary>
    /// One permission granted to a role. The admin role is implicit and does not use rows here.
    /// The primary key is (role, permission).
    /// </summary>
    [Table("role_permissions")]
    public class RolePermission
    {
        [Column("role")]
        [MaxLength(50)]
        public string Role { get; set; } = string.Empty;

        [Column("permission")]
        [MaxLength(100)]
        public string Permission { get; set; } = string.Empty;
    }
}
