using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace UnderMineApi.Migrations
{
    /// <inheritdoc />
    public partial class AddModpackImportToBuildInstances : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "build_preset_id",
                table: "build_instances",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "modpack_import",
                table: "build_instances",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "build_presets",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    game_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    loader_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    loader_version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_build_presets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "build_preset_mods",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    build_preset_id = table.Column<long>(type: "bigint", nullable: false),
                    source_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    target_folder = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    modrinth_slug_or_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    custom_file_name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    custom_file_s3_key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    sha256_hash = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    file_size = table.Column<long>(type: "bigint", nullable: true),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_build_preset_mods", x => x.id);
                    table.ForeignKey(
                        name: "FK_build_preset_mods_build_presets_build_preset_id",
                        column: x => x.build_preset_id,
                        principalTable: "build_presets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_build_instances_build_preset_id",
                table: "build_instances",
                column: "build_preset_id");

            migrationBuilder.CreateIndex(
                name: "IX_build_preset_mods_build_preset_id",
                table: "build_preset_mods",
                column: "build_preset_id");

            migrationBuilder.AddForeignKey(
                name: "FK_build_instances_build_presets_build_preset_id",
                table: "build_instances",
                column: "build_preset_id",
                principalTable: "build_presets",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_build_instances_build_presets_build_preset_id",
                table: "build_instances");

            migrationBuilder.DropTable(
                name: "build_preset_mods");

            migrationBuilder.DropTable(
                name: "build_presets");

            migrationBuilder.DropIndex(
                name: "IX_build_instances_build_preset_id",
                table: "build_instances");

            migrationBuilder.DropColumn(
                name: "build_preset_id",
                table: "build_instances");

            migrationBuilder.DropColumn(
                name: "modpack_import",
                table: "build_instances");
        }
    }
}
