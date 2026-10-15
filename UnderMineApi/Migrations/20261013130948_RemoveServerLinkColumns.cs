using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UnderMineApi.Migrations
{
    /// <inheritdoc />
    public partial class RemoveServerLinkColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "config_type_b_url",
                table: "server_configs");

            migrationBuilder.DropColumn(
                name: "installer_link",
                table: "server_configs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "config_type_b_url",
                table: "server_configs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "installer_link",
                table: "server_configs",
                type: "text",
                nullable: true);
        }
    }
}
