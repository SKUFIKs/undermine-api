using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UnderMineApi.Migrations
{
    /// <inheritdoc />
    public partial class AddRegistrationLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Device IDs are validated to <= 128 chars from now on. Existing rows may contain
            // client-supplied values of arbitrary length, so shorten them before the column
            // type changes (otherwise PostgreSQL rejects the ALTER).
            migrationBuilder.Sql(
                "UPDATE user_accounts SET device_id = left(device_id, 128) " +
                "WHERE device_id IS NOT NULL AND length(device_id) > 128;");

            migrationBuilder.AlterColumn<string>(
                name: "device_id",
                table: "user_accounts",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "regip_prefix",
                table: "user_accounts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_accounts_device_id",
                table: "user_accounts",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "IX_user_accounts_regip",
                table: "user_accounts",
                column: "regip");

            migrationBuilder.CreateIndex(
                name: "IX_user_accounts_regip_prefix",
                table: "user_accounts",
                column: "regip_prefix");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_user_accounts_device_id",
                table: "user_accounts");

            migrationBuilder.DropIndex(
                name: "IX_user_accounts_regip",
                table: "user_accounts");

            migrationBuilder.DropIndex(
                name: "IX_user_accounts_regip_prefix",
                table: "user_accounts");

            migrationBuilder.DropColumn(
                name: "regip_prefix",
                table: "user_accounts");

            migrationBuilder.AlterColumn<string>(
                name: "device_id",
                table: "user_accounts",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128,
                oldNullable: true);
        }
    }
}
