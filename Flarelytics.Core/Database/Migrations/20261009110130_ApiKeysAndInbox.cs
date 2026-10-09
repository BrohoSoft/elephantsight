using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flarelytics.Core.Database.Migrations
{
    /// <inheritdoc />
    public partial class ApiKeysAndInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ApiKeyId",
                table: "SocialPost",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalRef",
                table: "SocialPost",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsInbox",
                table: "SocialPost",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "SuggestedAtUtc",
                table: "SocialPost",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ApiKey",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Prefix = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    KeyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LastUsedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiKey", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApiKey_Tenant_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenant",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SocialPost_ApiKeyId",
                table: "SocialPost",
                column: "ApiKeyId");

            migrationBuilder.CreateIndex(
                name: "IX_SocialPost_TenantId_ExternalRef",
                table: "SocialPost",
                columns: new[] { "TenantId", "ExternalRef" },
                unique: true,
                filter: "\"ExternalRef\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SocialPost_TenantId_IsInbox",
                table: "SocialPost",
                columns: new[] { "TenantId", "IsInbox" });

            migrationBuilder.CreateIndex(
                name: "IX_ApiKey_KeyHash",
                table: "ApiKey",
                column: "KeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiKey_TenantId",
                table: "ApiKey",
                column: "TenantId");

            migrationBuilder.AddForeignKey(
                name: "FK_SocialPost_ApiKey_ApiKeyId",
                table: "SocialPost",
                column: "ApiKeyId",
                principalTable: "ApiKey",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SocialPost_ApiKey_ApiKeyId",
                table: "SocialPost");

            migrationBuilder.DropTable(
                name: "ApiKey");

            migrationBuilder.DropIndex(
                name: "IX_SocialPost_ApiKeyId",
                table: "SocialPost");

            migrationBuilder.DropIndex(
                name: "IX_SocialPost_TenantId_ExternalRef",
                table: "SocialPost");

            migrationBuilder.DropIndex(
                name: "IX_SocialPost_TenantId_IsInbox",
                table: "SocialPost");

            migrationBuilder.DropColumn(
                name: "ApiKeyId",
                table: "SocialPost");

            migrationBuilder.DropColumn(
                name: "ExternalRef",
                table: "SocialPost");

            migrationBuilder.DropColumn(
                name: "IsInbox",
                table: "SocialPost");

            migrationBuilder.DropColumn(
                name: "SuggestedAtUtc",
                table: "SocialPost");
        }
    }
}
