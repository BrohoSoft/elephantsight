using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flarelytics.Core.Database.Migrations
{
    /// <inheritdoc />
    public partial class MediaStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HasThumbnail",
                table: "SocialMedia",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Location",
                table: "SocialMedia",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "OriginalDeletedAtUtc",
                table: "SocialMedia",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HasThumbnail",
                table: "SocialMedia");

            migrationBuilder.DropColumn(
                name: "Location",
                table: "SocialMedia");

            migrationBuilder.DropColumn(
                name: "OriginalDeletedAtUtc",
                table: "SocialMedia");
        }
    }
}
