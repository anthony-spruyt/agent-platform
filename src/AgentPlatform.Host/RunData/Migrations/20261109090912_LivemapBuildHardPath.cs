using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentPlatform.Host.RunData.Migrations
{
    /// <inheritdoc />
    public partial class LivemapBuildHardPath : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "agent_runs",
                table: "livemap_builds",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "patch_base_sha",
                table: "livemap_builds",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "patch_notes",
                table: "livemap_builds",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "patch_result",
                table: "livemap_builds",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "agent_runs",
                table: "livemap_builds");

            migrationBuilder.DropColumn(
                name: "patch_base_sha",
                table: "livemap_builds");

            migrationBuilder.DropColumn(
                name: "patch_notes",
                table: "livemap_builds");

            migrationBuilder.DropColumn(
                name: "patch_result",
                table: "livemap_builds");
        }
    }
}
