using Flarelytics.Core.Database;
﻿using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flarelytics.Core.Database.Migrations
{
    /// <inheritdoc />
    public partial class Reviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Review",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Store = table.Column<int>(type: "integer", nullable: false),
                    AppId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Rating = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Body = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    Author = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Locale = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    AppVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    WrittenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReplyText = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    RepliedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReplyExternalId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ReplyState = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Review", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Review_Tenant_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenant",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReviewSyncState",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Store = table.Column<int>(type: "integer", nullable: false),
                    AppId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    LastSyncedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReviewSyncState", x => new { x.TenantId, x.Store, x.AppId });
                    table.ForeignKey(
                        name: "FK_ReviewSyncState_Tenant_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenant",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Review_TenantId_Store_ExternalId",
                table: "Review",
                columns: new[] { "TenantId", "Store", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Review_TenantId_WrittenAtUtc",
                table: "Review",
                columns: new[] { "TenantId", "WrittenAtUtc" });

            RowLevelSecurity.Enable(migrationBuilder, "Review");
            RowLevelSecurity.Enable(migrationBuilder, "ReviewSyncState");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Review");

            migrationBuilder.DropTable(
                name: "ReviewSyncState");
        }
    }
}
