using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flarelytics.Core.Database.Migrations
{
    /// <inheritdoc />
    public partial class ProjectAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<Guid>>(
                name: "ProjectIds",
                table: "SocialAccount",
                type: "uuid[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<bool>(
                name: "AllProjects",
                table: "Membership",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<List<Guid>>(
                name: "ProjectIds",
                table: "Membership",
                type: "uuid[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<int>(
                name: "Sections",
                table: "Membership",
                type: "integer",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<bool>(
                name: "AllProjects",
                table: "Invitation",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<List<Guid>>(
                name: "ProjectIds",
                table: "Invitation",
                type: "uuid[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<int>(
                name: "Sections",
                table: "Invitation",
                type: "integer",
                nullable: false,
                defaultValue: 3);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProjectIds",
                table: "SocialAccount");

            migrationBuilder.DropColumn(
                name: "AllProjects",
                table: "Membership");

            migrationBuilder.DropColumn(
                name: "ProjectIds",
                table: "Membership");

            migrationBuilder.DropColumn(
                name: "Sections",
                table: "Membership");

            migrationBuilder.DropColumn(
                name: "AllProjects",
                table: "Invitation");

            migrationBuilder.DropColumn(
                name: "ProjectIds",
                table: "Invitation");

            migrationBuilder.DropColumn(
                name: "Sections",
                table: "Invitation");
        }
    }
}
