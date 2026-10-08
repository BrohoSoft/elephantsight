using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flarelytics.Core.Database.Migrations
{
    /// <inheritdoc />
    public partial class GooglePlayInstalls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ReportFile_CredentialId_Kind_ReportDate",
                table: "ReportFile");

            migrationBuilder.AlterColumn<string>(
                name: "RelativePath",
                table: "ReportFile",
                type: "character varying(400)",
                maxLength: 400,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(300)",
                oldMaxLength: 300,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContentHash",
                table: "ReportFile",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Scope",
                table: "ReportFile",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Uninstalls",
                schema: "metrics",
                table: "DailyAppMetric",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_ReportFile_CredentialId_Kind_ReportDate_Scope",
                table: "ReportFile",
                columns: new[] { "CredentialId", "Kind", "ReportDate", "Scope" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ReportFile_CredentialId_Kind_ReportDate_Scope",
                table: "ReportFile");

            migrationBuilder.DropColumn(
                name: "ContentHash",
                table: "ReportFile");

            migrationBuilder.DropColumn(
                name: "Scope",
                table: "ReportFile");

            migrationBuilder.DropColumn(
                name: "Uninstalls",
                schema: "metrics",
                table: "DailyAppMetric");

            migrationBuilder.AlterColumn<string>(
                name: "RelativePath",
                table: "ReportFile",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(400)",
                oldMaxLength: 400,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReportFile_CredentialId_Kind_ReportDate",
                table: "ReportFile",
                columns: new[] { "CredentialId", "Kind", "ReportDate" },
                unique: true);
        }
    }
}
