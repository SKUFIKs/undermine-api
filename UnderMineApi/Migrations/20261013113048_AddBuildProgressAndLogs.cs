using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace UnderMineApi.Migrations
{
    /// <inheritdoc />
    public partial class AddBuildProgressAndLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "bytes_downloaded",
                table: "build_instances",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "current_file",
                table: "build_instances",
                type: "character varying(800)",
                maxLength: 800,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "current_step",
                table: "build_instances",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "download_speed",
                table: "build_instances",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "downloaded_files",
                table: "build_instances",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "progress_percent",
                table: "build_instances",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "total_bytes",
                table: "build_instances",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "total_files",
                table: "build_instances",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "build_logs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    build_instance_id = table.Column<long>(type: "bigint", nullable: false),
                    level = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    message = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_build_logs", x => x.id);
                    table.ForeignKey(
                        name: "FK_build_logs_build_instances_build_instance_id",
                        column: x => x.build_instance_id,
                        principalTable: "build_instances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_build_logs_build_instance_id",
                table: "build_logs",
                column: "build_instance_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "build_logs");

            migrationBuilder.DropColumn(
                name: "bytes_downloaded",
                table: "build_instances");

            migrationBuilder.DropColumn(
                name: "current_file",
                table: "build_instances");

            migrationBuilder.DropColumn(
                name: "current_step",
                table: "build_instances");

            migrationBuilder.DropColumn(
                name: "download_speed",
                table: "build_instances");

            migrationBuilder.DropColumn(
                name: "downloaded_files",
                table: "build_instances");

            migrationBuilder.DropColumn(
                name: "progress_percent",
                table: "build_instances");

            migrationBuilder.DropColumn(
                name: "total_bytes",
                table: "build_instances");

            migrationBuilder.DropColumn(
                name: "total_files",
                table: "build_instances");
        }
    }
}
