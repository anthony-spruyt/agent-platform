using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentPlatform.Host.RunData.Migrations
{
    /// <inheritdoc />
    public partial class LivemapBuildProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "attempts",
                table: "livemap_builds",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "classification",
                table: "livemap_builds",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "compat_run_id",
                table: "livemap_builds",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "detail",
                table: "livemap_builds",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "next_retry_at",
                table: "livemap_builds",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "version",
                table: "livemap_builds",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "attempts",
                table: "livemap_builds");

            migrationBuilder.DropColumn(
                name: "classification",
                table: "livemap_builds");

            migrationBuilder.DropColumn(
                name: "compat_run_id",
                table: "livemap_builds");

            migrationBuilder.DropColumn(
                name: "detail",
                table: "livemap_builds");

            migrationBuilder.DropColumn(
                name: "next_retry_at",
                table: "livemap_builds");

            migrationBuilder.DropColumn(
                name: "version",
                table: "livemap_builds");
        }
    }
}
