using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace UnderMineApi.Migrations
{
    /// <inheritdoc />
    public partial class AddUserIpHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_ip_history",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    first_seen = table.Column<long>(type: "bigint", nullable: false),
                    last_seen = table.Column<long>(type: "bigint", nullable: false),
                    seen_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_ip_history", x => x.id);
                    table.ForeignKey(
                        name: "FK_user_ip_history_user_accounts_user_id",
                        column: x => x.user_id,
                        principalTable: "user_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_user_ip_history_user_id_ip",
                table: "user_ip_history",
                columns: new[] { "user_id", "ip" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_ip_history_user_id_last_seen",
                table: "user_ip_history",
                columns: new[] { "user_id", "last_seen" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_ip_history");
        }
    }
}
