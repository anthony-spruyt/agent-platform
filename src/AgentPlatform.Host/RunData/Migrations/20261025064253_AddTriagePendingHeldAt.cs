using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentPlatform.Host.RunData.Migrations
{
    /// <inheritdoc />
    public partial class AddTriagePendingHeldAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "held_at",
                table: "renovate_triage_pending",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "held_at",
                table: "renovate_triage_pending");
        }
    }
}
