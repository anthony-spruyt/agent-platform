using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentPlatform.Host.RunData.Migrations
{
    /// <inheritdoc />
    public partial class LivemapBuildRelease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "merged_at",
                table: "livemap_builds",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "notified_at",
                table: "livemap_builds",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "pin_attempts",
                table: "livemap_builds",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "pin_head_sha",
                table: "livemap_builds",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "pin_pull_request_number",
                table: "livemap_builds",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "pinned_at",
                table: "livemap_builds",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "release_sha256",
                table: "livemap_builds",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "release_tag",
                table: "livemap_builds",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "released_at",
                table: "livemap_builds",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "merged_at",
                table: "livemap_builds");

            migrationBuilder.DropColumn(
                name: "notified_at",
                table: "livemap_builds");

            migrationBuilder.DropColumn(
                name: "pin_attempts",
                table: "livemap_builds");

            migrationBuilder.DropColumn(
                name: "pin_head_sha",
                table: "livemap_builds");

            migrationBuilder.DropColumn(
                name: "pin_pull_request_number",
                table: "livemap_builds");

            migrationBuilder.DropColumn(
                name: "pinned_at",
                table: "livemap_builds");

            migrationBuilder.DropColumn(
                name: "release_sha256",
                table: "livemap_builds");

            migrationBuilder.DropColumn(
                name: "release_tag",
                table: "livemap_builds");

            migrationBuilder.DropColumn(
                name: "released_at",
                table: "livemap_builds");
        }
    }
}
