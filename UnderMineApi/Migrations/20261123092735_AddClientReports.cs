using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace UnderMineApi.Migrations
{
    /// <inheritdoc />
    public partial class AddClientReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "client_reports",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: true),
                    device_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    summary = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    launcher_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    minecraft_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    loader = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    server_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    os = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    admin_note = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    consent_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<long>(type: "bigint", nullable: false),
                    resolved_at = table.Column<long>(type: "bigint", nullable: true),
                    resolved_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_client_reports", x => x.id);
                    table.ForeignKey(
                        name: "FK_client_reports_user_accounts_user_id",
                        column: x => x.user_id,
                        principalTable: "user_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "client_report_files",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    report_id = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    data = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_client_report_files", x => x.id);
                    table.ForeignKey(
                        name: "FK_client_report_files_client_reports_report_id",
                        column: x => x.report_id,
                        principalTable: "client_reports",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "role_permissions",
                columns: new[] { "permission", "role" },
                values: new object[,]
                {
                    { "reports.manage", "moderator" },
                    { "reports.view", "moderator" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_client_report_files_report_id",
                table: "client_report_files",
                column: "report_id");

            migrationBuilder.CreateIndex(
                name: "IX_client_reports_created_at",
                table: "client_reports",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_client_reports_device_id",
                table: "client_reports",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "IX_client_reports_status_created_at",
                table: "client_reports",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_client_reports_user_id",
                table: "client_reports",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "client_report_files");

            migrationBuilder.DropTable(
                name: "client_reports");

            migrationBuilder.DeleteData(
                table: "role_permissions",
                keyColumns: new[] { "permission", "role" },
                keyValues: new object[] { "reports.manage", "moderator" });

            migrationBuilder.DeleteData(
                table: "role_permissions",
                keyColumns: new[] { "permission", "role" },
                keyValues: new object[] { "reports.view", "moderator" });
        }
    }
}
