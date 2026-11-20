using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UnderMineApi.Migrations
{
    /// <inheritdoc />
    public partial class AddBuildQueryIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_build_logs_created_at",
                table: "build_logs",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_build_instances_created_at",
                table: "build_instances",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_build_instances_status_created_at",
                table: "build_instances",
                columns: new[] { "status", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_build_logs_created_at",
                table: "build_logs");

            migrationBuilder.DropIndex(
                name: "IX_build_instances_created_at",
                table: "build_instances");

            migrationBuilder.DropIndex(
                name: "IX_build_instances_status_created_at",
                table: "build_instances");
        }
    }
}
