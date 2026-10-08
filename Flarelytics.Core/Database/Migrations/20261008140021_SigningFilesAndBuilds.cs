using Flarelytics.Core.Database;
﻿using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flarelytics.Core.Database.Migrations
{
    /// <inheritdoc />
    public partial class SigningFilesAndBuilds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BuildUpload",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Store = table.Column<int>(type: "integer", nullable: false),
                    AppId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CredentialId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    StoragePath = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    Version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    BuildNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Track = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ReleaseStatus = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    RolloutPercent = table.Column<double>(type: "double precision", nullable: true),
                    ReleaseNotesLanguage = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ReleaseNotes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ExternalId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BuildUpload", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BuildUpload_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BuildUpload_Tenant_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenant",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectSecretFile",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Platform = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    KeyVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LastDownloadedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastDownloadedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectSecretFile", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectSecretFile_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProjectSecretFile_Tenant_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenant",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BuildUpload_ProjectId",
                table: "BuildUpload",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_BuildUpload_TenantId_Status",
                table: "BuildUpload",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectSecretFile_ProjectId",
                table: "ProjectSecretFile",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectSecretFile_TenantId",
                table: "ProjectSecretFile",
                column: "TenantId");

            RowLevelSecurity.Enable(migrationBuilder, "ProjectSecretFile");
            RowLevelSecurity.Enable(migrationBuilder, "BuildUpload");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BuildUpload");

            migrationBuilder.DropTable(
                name: "ProjectSecretFile");
        }
    }
}
