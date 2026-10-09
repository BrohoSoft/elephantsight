using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flarelytics.Core.Database.Migrations
{
    /// <inheritdoc />
    public partial class InstanceSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsInstanceAdmin",
                table: "User",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "InstanceSetting",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ProtectedValue = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstanceSetting", x => x.Key);
                });

            // Chi ha fatto l'installazione (il primo utente) amministra l'istanza.
            migrationBuilder.Sql("""
                UPDATE "User" SET "IsInstanceAdmin" = true
                WHERE "Id" = (SELECT "Id" FROM "User" ORDER BY "CreatedAtUtc", "Id" LIMIT 1);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InstanceSetting");

            migrationBuilder.DropColumn(
                name: "IsInstanceAdmin",
                table: "User");
        }
    }
}
