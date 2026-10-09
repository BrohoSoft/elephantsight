using Flarelytics.Core.Database;
using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flarelytics.Core.Database.Migrations
{
    /// <inheritdoc />
    public partial class RecurringPostsAndThreads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RecurringPostId",
                table: "SocialPost",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RecurringPostId",
                table: "SocialMedia",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SocialRecurringPost",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    Text = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    OptionsJson = table.Column<string>(type: "jsonb", nullable: true),
                    AccountIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    Frequency = table.Column<int>(type: "integer", nullable: false),
                    Interval = table.Column<int>(type: "integer", nullable: false),
                    DaysOfWeek = table.Column<int>(type: "integer", nullable: false),
                    TimeOfDay = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    TimeZone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    IsPaused = table.Column<bool>(type: "boolean", nullable: false),
                    NextOccurrenceUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastOccurrenceUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OccurrenceCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocialRecurringPost", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SocialRecurringPost_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_SocialRecurringPost_Tenant_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenant",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SocialPost_RecurringPostId",
                table: "SocialPost",
                column: "RecurringPostId");

            migrationBuilder.CreateIndex(
                name: "IX_SocialMedia_RecurringPostId",
                table: "SocialMedia",
                column: "RecurringPostId");

            migrationBuilder.CreateIndex(
                name: "IX_SocialRecurringPost_ProjectId",
                table: "SocialRecurringPost",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_SocialRecurringPost_TenantId_NextOccurrenceUtc",
                table: "SocialRecurringPost",
                columns: new[] { "TenantId", "NextOccurrenceUtc" });

            migrationBuilder.AddForeignKey(
                name: "FK_SocialMedia_SocialRecurringPost_RecurringPostId",
                table: "SocialMedia",
                column: "RecurringPostId",
                principalTable: "SocialRecurringPost",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SocialPost_SocialRecurringPost_RecurringPostId",
                table: "SocialPost",
                column: "RecurringPostId",
                principalTable: "SocialRecurringPost",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            RowLevelSecurity.Enable(migrationBuilder, "SocialRecurringPost");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SocialMedia_SocialRecurringPost_RecurringPostId",
                table: "SocialMedia");

            migrationBuilder.DropForeignKey(
                name: "FK_SocialPost_SocialRecurringPost_RecurringPostId",
                table: "SocialPost");

            migrationBuilder.DropTable(
                name: "SocialRecurringPost");

            migrationBuilder.DropIndex(
                name: "IX_SocialPost_RecurringPostId",
                table: "SocialPost");

            migrationBuilder.DropIndex(
                name: "IX_SocialMedia_RecurringPostId",
                table: "SocialMedia");

            migrationBuilder.DropColumn(
                name: "RecurringPostId",
                table: "SocialPost");

            migrationBuilder.DropColumn(
                name: "RecurringPostId",
                table: "SocialMedia");
        }
    }
}
