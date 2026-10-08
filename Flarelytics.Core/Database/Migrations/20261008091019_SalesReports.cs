using Flarelytics.Core.Database;
﻿using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flarelytics.Core.Database.Migrations
{
    /// <inheritdoc />
    public partial class SalesReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "metrics");

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSyncCompletedAtUtc",
                table: "StoreCredential",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastSyncError",
                table: "StoreCredential",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SyncRequestedAtUtc",
                table: "StoreCredential",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AppleAppSku",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sku = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CredentialId = table.Column<Guid>(type: "uuid", nullable: false),
                    AppleId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppleAppSku", x => new { x.TenantId, x.Sku });
                    table.ForeignKey(
                        name: "FK_AppleAppSku_StoreCredential_CredentialId",
                        column: x => x.CredentialId,
                        principalTable: "StoreCredential",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AppleAppSku_Tenant_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenant",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DailyAppMetric",
                schema: "metrics",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Store = table.Column<int>(type: "integer", nullable: false),
                    AppId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    CountryCode = table.Column<string>(type: "character(2)", fixedLength: true, maxLength: 2, nullable: false),
                    Downloads = table.Column<int>(type: "integer", nullable: false),
                    Redownloads = table.Column<int>(type: "integer", nullable: false),
                    Updates = table.Column<int>(type: "integer", nullable: false),
                    InAppPurchases = table.Column<int>(type: "integer", nullable: false),
                    Refunds = table.Column<int>(type: "integer", nullable: false),
                    ProceedsEurMicros = table.Column<long>(type: "bigint", nullable: false),
                    SalesEurMicros = table.Column<long>(type: "bigint", nullable: false),
                    HasUnconvertedAmounts = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyAppMetric", x => new { x.TenantId, x.Store, x.AppId, x.Date, x.CountryCode });
                    table.ForeignKey(
                        name: "FK_DailyAppMetric_Tenant_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenant",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExchangeRate",
                columns: table => new
                {
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    UnitsPerEuro = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExchangeRate", x => new { x.Currency, x.Date });
                });

            migrationBuilder.CreateTable(
                name: "ReportFile",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CredentialId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    ReportDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    RelativePath = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    FetchedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ProcessedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ParserVersion = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportFile", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReportFile_StoreCredential_CredentialId",
                        column: x => x.CredentialId,
                        principalTable: "StoreCredential",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ReportFile_Tenant_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenant",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppleAppSku_CredentialId",
                table: "AppleAppSku",
                column: "CredentialId");

            migrationBuilder.CreateIndex(
                name: "IX_DailyAppMetric_TenantId_Date",
                schema: "metrics",
                table: "DailyAppMetric",
                columns: new[] { "TenantId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_ReportFile_CredentialId_Kind_ReportDate",
                table: "ReportFile",
                columns: new[] { "CredentialId", "Kind", "ReportDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReportFile_TenantId",
                table: "ReportFile",
                column: "TenantId");

            // Tabelle dei tenant: vedi RowLevelSecurity.
            RowLevelSecurity.Enable(migrationBuilder, "ReportFile");
            RowLevelSecurity.Enable(migrationBuilder, "AppleAppSku");
            RowLevelSecurity.Enable(migrationBuilder, "DailyAppMetric", "metrics");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            RowLevelSecurity.Disable(migrationBuilder, "DailyAppMetric", "metrics");
            RowLevelSecurity.Disable(migrationBuilder, "AppleAppSku");
            RowLevelSecurity.Disable(migrationBuilder, "ReportFile");

            migrationBuilder.DropTable(
                name: "AppleAppSku");

            migrationBuilder.DropTable(
                name: "DailyAppMetric",
                schema: "metrics");

            migrationBuilder.DropTable(
                name: "ExchangeRate");

            migrationBuilder.DropTable(
                name: "ReportFile");

            migrationBuilder.DropColumn(
                name: "LastSyncCompletedAtUtc",
                table: "StoreCredential");

            migrationBuilder.DropColumn(
                name: "LastSyncError",
                table: "StoreCredential");

            migrationBuilder.DropColumn(
                name: "SyncRequestedAtUtc",
                table: "StoreCredential");
        }
    }
}
