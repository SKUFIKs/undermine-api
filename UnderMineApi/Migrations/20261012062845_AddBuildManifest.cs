using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UnderMineApi.Migrations
{
    /// <inheritdoc />
    public partial class AddBuildManifest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "build_manifest",
                table: "server_manifests",
                type: "character varying(1000000)",
                maxLength: 1000000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "build_manifest",
                table: "server_manifests");
        }
    }
}
