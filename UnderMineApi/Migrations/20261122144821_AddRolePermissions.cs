using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace UnderMineApi.Migrations
{
    /// <inheritdoc />
    public partial class AddRolePermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "role_permissions",
                columns: table => new
                {
                    role = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    permission = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_role_permissions", x => new { x.role, x.permission });
                });

            migrationBuilder.InsertData(
                table: "role_permissions",
                columns: new[] { "permission", "role" },
                values: new object[,]
                {
                    { "builds.upload", "moderator" },
                    { "presets.create", "moderator" },
                    { "servers.create", "moderator" },
                    { "servers.edit", "moderator" },
                    { "servers.view", "moderator" },
                    { "users.ban", "moderator" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "role_permissions");
        }
    }
}
