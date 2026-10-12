using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentPlatform.Host.RunData.Migrations
{
    /// <inheritdoc />
    public partial class AddTriageVerdictModeKeyAndAppliedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_renovate_triage_verdicts_repo_pr_number_head_sha",
                table: "renovate_triage_verdicts");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "applied_at",
                table: "renovate_triage_verdicts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_renovate_triage_verdicts_repo_pr_number_head_sha_mode",
                table: "renovate_triage_verdicts",
                columns: new[] { "repo", "pr_number", "head_sha", "mode" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_renovate_triage_verdicts_repo_pr_number_head_sha_mode",
                table: "renovate_triage_verdicts");

            migrationBuilder.DropColumn(
                name: "applied_at",
                table: "renovate_triage_verdicts");

            migrationBuilder.CreateIndex(
                name: "IX_renovate_triage_verdicts_repo_pr_number_head_sha",
                table: "renovate_triage_verdicts",
                columns: new[] { "repo", "pr_number", "head_sha" },
                unique: true);
        }
    }
}
