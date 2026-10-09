using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flarelytics.Core.Database.Migrations
{
    /// <inheritdoc />
    public partial class VideoAndTikTok : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ProgressStartedAtUtc",
                table: "SocialPostTarget",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OptionsJson",
                table: "SocialPost",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContentType",
                table: "SocialMedia",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "image/jpeg");

            migrationBuilder.AddColumn<int>(
                name: "DurationMs",
                table: "SocialMedia",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "FastStart",
                table: "SocialMedia",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "SocialMedia",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProgressStartedAtUtc",
                table: "SocialPostTarget");

            migrationBuilder.DropColumn(
                name: "OptionsJson",
                table: "SocialPost");

            migrationBuilder.DropColumn(
                name: "ContentType",
                table: "SocialMedia");

            migrationBuilder.DropColumn(
                name: "DurationMs",
                table: "SocialMedia");

            migrationBuilder.DropColumn(
                name: "FastStart",
                table: "SocialMedia");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "SocialMedia");
        }
    }
}
