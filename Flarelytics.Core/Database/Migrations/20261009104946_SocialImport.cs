using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flarelytics.Core.Database.Migrations
{
    /// <inheritdoc />
    public partial class SocialImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsImported",
                table: "SocialPost",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastImportAtUtc",
                table: "SocialAccount",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SocialPostTarget_TenantId_Network_ExternalId",
                table: "SocialPostTarget",
                columns: new[] { "TenantId", "Network", "ExternalId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SocialPostTarget_TenantId_Network_ExternalId",
                table: "SocialPostTarget");

            migrationBuilder.DropColumn(
                name: "IsImported",
                table: "SocialPost");

            migrationBuilder.DropColumn(
                name: "LastImportAtUtc",
                table: "SocialAccount");
        }
    }
}
