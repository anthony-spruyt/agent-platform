using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentPlatform.Host.RunData.Migrations
{
    /// <inheritdoc />
    public partial class GeneralizeClaudeBudgetKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_claude_runs_repo_finished_at",
                table: "claude_runs");

            migrationBuilder.DropIndex(
                name: "IX_claude_runs_repo_pr_number_head_sha",
                table: "claude_runs");

            migrationBuilder.RenameColumn(
                name: "repo",
                table: "claude_runs",
                newName: "scope");

            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "claude_runs",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "revision",
                table: "claude_runs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "subject",
                table: "claude_runs",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.Sql("UPDATE claude_runs SET kind = 'RenovateTriage', subject = scope || '#' || pr_number, revision = head_sha");

            migrationBuilder.AlterColumn<string>(
                name: "kind",
                table: "claude_runs",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "subject",
                table: "claude_runs",
                type: "character varying(300)",
                maxLength: 300,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(300)",
                oldMaxLength: 300,
                oldNullable: true);

            migrationBuilder.DropColumn(
                name: "head_sha",
                table: "claude_runs");

            migrationBuilder.DropColumn(
                name: "pr_number",
                table: "claude_runs");

            migrationBuilder.CreateIndex(
                name: "IX_claude_runs_kind_scope_finished_at",
                table: "claude_runs",
                columns: new[] { "kind", "scope", "finished_at" });

            migrationBuilder.CreateIndex(
                name: "IX_claude_runs_kind_subject_revision",
                table: "claude_runs",
                columns: new[] { "kind", "subject", "revision" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_claude_runs_kind_scope_finished_at",
                table: "claude_runs");

            migrationBuilder.DropIndex(
                name: "IX_claude_runs_kind_subject_revision",
                table: "claude_runs");

            // The old table can only hold Renovate runs.
            migrationBuilder.Sql("DELETE FROM claude_runs WHERE kind <> 'RenovateTriage'");

            migrationBuilder.AddColumn<string>(
                name: "head_sha",
                table: "claude_runs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "pr_number",
                table: "claude_runs",
                type: "integer",
                nullable: true);

            migrationBuilder.Sql("UPDATE claude_runs SET head_sha = COALESCE(revision, ''), pr_number = CAST(substring(subject FROM '#([0-9]+)$') AS integer)");

            migrationBuilder.AlterColumn<string>(
                name: "head_sha",
                table: "claude_runs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "pr_number",
                table: "claude_runs",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.DropColumn(
                name: "kind",
                table: "claude_runs");

            migrationBuilder.DropColumn(
                name: "revision",
                table: "claude_runs");

            migrationBuilder.DropColumn(
                name: "subject",
                table: "claude_runs");

            migrationBuilder.RenameColumn(
                name: "scope",
                table: "claude_runs",
                newName: "repo");

            migrationBuilder.CreateIndex(
                name: "IX_claude_runs_repo_finished_at",
                table: "claude_runs",
                columns: new[] { "repo", "finished_at" });

            migrationBuilder.CreateIndex(
                name: "IX_claude_runs_repo_pr_number_head_sha",
                table: "claude_runs",
                columns: new[] { "repo", "pr_number", "head_sha" });
        }
    }
}
