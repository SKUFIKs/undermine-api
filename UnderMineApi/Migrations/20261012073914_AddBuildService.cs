using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace UnderMineApi.Migrations
{
    /// <inheritdoc />
    public partial class AddBuildService : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "build_instances",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    mc_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    loader_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    loader_version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    build_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    manifest_json = table.Column<string>(type: "text", nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_build_instances", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "build_files",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    build_id = table.Column<long>(type: "bigint", nullable: false),
                    path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    sha256 = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    policy = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    s3_key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_build_files", x => x.id);
                    table.ForeignKey(
                        name: "FK_build_files_build_instances_build_id",
                        column: x => x.build_id,
                        principalTable: "build_instances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_build_files_build_id",
                table: "build_files",
                column: "build_id");

            migrationBuilder.CreateIndex(
                name: "IX_build_files_sha256",
                table: "build_files",
                column: "sha256");

            migrationBuilder.CreateIndex(
                name: "IX_build_instances_build_id",
                table: "build_instances",
                column: "build_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "build_files");

            migrationBuilder.DropTable(
                name: "build_instances");
        }
    }
}
