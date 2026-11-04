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
                name: "scope",
                table: "claude_runs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "subject",
                table: "claude_runs",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.Sql("UPDATE claude_runs SET kind = 'RenovateTriage', scope = repo, subject = repo || '#' || pr_number, revision = head_sha");

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
                name: "scope",
                table: "claude_runs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
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

            migrationBuilder.AlterColumn<string>(
                name: "repo",
                table: "claude_runs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            migrationBuilder.AlterColumn<int>(
                name: "pr_number",
                table: "claude_runs",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<string>(
                name: "head_sha",
                table: "claude_runs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);

            migrationBuilder.CreateIndex(
                name: "IX_claude_runs_kind_scope_finished_at",
                table: "claude_runs",
                columns: new[] { "kind", "scope", "finished_at" });

            migrationBuilder.CreateIndex(
                name: "IX_claude_runs_kind_subject_revision",
                table: "claude_runs",
                columns: new[] { "kind", "subject", "revision" });

            // Inserts that name only repo, pr_number and head_sha (the previous image) get the new key columns filled, so the new code counts those rows too.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION claude_runs_fill_generalized_key() RETURNS trigger AS $$
                BEGIN
                    NEW.kind := 'RenovateTriage';
                    NEW.scope := NEW.repo;
                    NEW.subject := NEW.repo || '#' || NEW.pr_number;
                    NEW.revision := NEW.head_sha;
                    RETURN NEW;
                END
                $$ LANGUAGE plpgsql
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER claude_runs_fill_generalized_key BEFORE INSERT ON claude_runs
                FOR EACH ROW WHEN (NEW.kind IS NULL)
                EXECUTE FUNCTION claude_runs_fill_generalized_key()
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER claude_runs_fill_generalized_key ON claude_runs");

            migrationBuilder.Sql("DROP FUNCTION claude_runs_fill_generalized_key()");

            migrationBuilder.DropIndex(
                name: "IX_claude_runs_kind_scope_finished_at",
                table: "claude_runs");

            migrationBuilder.DropIndex(
                name: "IX_claude_runs_kind_subject_revision",
                table: "claude_runs");

            // The old table can only hold Renovate runs.
            migrationBuilder.Sql("DELETE FROM claude_runs WHERE kind <> 'RenovateTriage'");

            migrationBuilder.Sql("UPDATE claude_runs SET repo = COALESCE(repo, scope), pr_number = COALESCE(pr_number, CAST(substring(subject FROM '#([0-9]+)$') AS integer)), head_sha = COALESCE(head_sha, revision, '')");

            migrationBuilder.AlterColumn<string>(
                name: "repo",
                table: "claude_runs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "pr_number",
                table: "claude_runs",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

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

            migrationBuilder.DropColumn(
                name: "kind",
                table: "claude_runs");

            migrationBuilder.DropColumn(
                name: "revision",
                table: "claude_runs");

            migrationBuilder.DropColumn(
                name: "scope",
                table: "claude_runs");

            migrationBuilder.DropColumn(
                name: "subject",
                table: "claude_runs");
        }
    }
}
