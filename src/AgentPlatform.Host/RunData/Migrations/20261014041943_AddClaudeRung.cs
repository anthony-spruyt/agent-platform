using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentPlatform.Host.RunData.Migrations
{
    /// <inheritdoc />
    public partial class AddClaudeRung : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "action",
                table: "renovate_triage_verdicts",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "rung",
                table: "renovate_triage_verdicts",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "claude_cooldowns",
                columns: table => new
                {
                    scope = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cause = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_claude_cooldowns", x => x.scope);
                });

            migrationBuilder.CreateTable(
                name: "claude_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    repo = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    pr_number = table.Column<int>(type: "integer", nullable: false),
                    head_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    workflow_id = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    reserved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    pod_created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    input_tokens = table.Column<long>(type: "bigint", nullable: true),
                    output_tokens = table.Column<long>(type: "bigint", nullable: true),
                    cost_usd = table.Column<decimal>(type: "numeric(18,8)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_claude_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "renovate_triage_pending",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    repo = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    pr_number = table.Column<int>(type: "integer", nullable: false),
                    head_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_renovate_triage_pending", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sweeper_state",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    last_seen_delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_success_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sweeper_state", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_claude_runs_pod_created_at",
                table: "claude_runs",
                column: "pod_created_at");

            migrationBuilder.CreateIndex(
                name: "IX_claude_runs_repo_finished_at",
                table: "claude_runs",
                columns: new[] { "repo", "finished_at" });

            migrationBuilder.CreateIndex(
                name: "IX_claude_runs_repo_pr_number_head_sha",
                table: "claude_runs",
                columns: new[] { "repo", "pr_number", "head_sha" });

            migrationBuilder.CreateIndex(
                name: "IX_renovate_triage_pending_repo_pr_number",
                table: "renovate_triage_pending",
                columns: new[] { "repo", "pr_number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "claude_cooldowns");

            migrationBuilder.DropTable(
                name: "claude_runs");

            migrationBuilder.DropTable(
                name: "renovate_triage_pending");

            migrationBuilder.DropTable(
                name: "sweeper_state");

            migrationBuilder.DropColumn(
                name: "action",
                table: "renovate_triage_verdicts");

            migrationBuilder.DropColumn(
                name: "rung",
                table: "renovate_triage_verdicts");
        }
    }
}
