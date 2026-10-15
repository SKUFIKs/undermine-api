using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UnderMineApi.Migrations
{
    /// <inheritdoc />
    public partial class SplitServerManifestAndConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "config_hash_sum",
                table: "server_manifests");

            migrationBuilder.DropColumn(
                name: "config_type_a_url",
                table: "server_manifests");

            migrationBuilder.DropColumn(
                name: "config_type_b_url",
                table: "server_manifests");

            migrationBuilder.DropColumn(
                name: "config_type_c_url",
                table: "server_manifests");

            migrationBuilder.DropColumn(
                name: "installer_link",
                table: "server_manifests");

            migrationBuilder.RenameColumn(
                name: "type_version",
                table: "server_manifests",
                newName: "ip_address");

            migrationBuilder.CreateTable(
                name: "server_configs",
                columns: table => new
                {
                    server_id = table.Column<long>(type: "bigint", nullable: false),
                    type_version = table.Column<string>(type: "text", nullable: false),
                    installer_link = table.Column<string>(type: "text", nullable: true),
                    config_type_a_url = table.Column<string>(type: "text", nullable: true),
                    config_type_b_url = table.Column<string>(type: "text", nullable: true),
                    config_type_c_url = table.Column<string>(type: "text", nullable: true),
                    config_hash_sum = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_server_configs", x => x.server_id);
                    table.ForeignKey(
                        name: "FK_server_configs_server_manifests_server_id",
                        column: x => x.server_id,
                        principalTable: "server_manifests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "server_configs");

            migrationBuilder.RenameColumn(
                name: "ip_address",
                table: "server_manifests",
                newName: "type_version");

            migrationBuilder.AddColumn<string>(
                name: "config_hash_sum",
                table: "server_manifests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "config_type_a_url",
                table: "server_manifests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "config_type_b_url",
                table: "server_manifests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "config_type_c_url",
                table: "server_manifests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "installer_link",
                table: "server_manifests",
                type: "text",
                nullable: true);
        }
    }
}
