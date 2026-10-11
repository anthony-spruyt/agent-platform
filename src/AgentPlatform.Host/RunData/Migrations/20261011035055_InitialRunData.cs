using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AgentPlatform.Host.RunData.Migrations
{
    /// <inheritdoc />
    public partial class InitialRunData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "prompts",
                columns: table => new
                {
                    hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_prompts", x => x.hash);
                });

            migrationBuilder.CreateTable(
                name: "runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workflow_id = table.Column<string>(type: "text", nullable: false),
                    workflow_run_id = table.Column<string>(type: "text", nullable: false),
                    workflow_type = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    input_event = table.Column<string>(type: "jsonb", nullable: false),
                    context = table.Column<string>(type: "jsonb", nullable: true),
                    prompt_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    model = table.Column<string>(type: "text", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    duration_ms = table.Column<long>(type: "bigint", nullable: true),
                    input_tokens = table.Column<long>(type: "bigint", nullable: true),
                    output_tokens = table.Column<long>(type: "bigint", nullable: true),
                    cost_usd = table.Column<decimal>(type: "numeric(18,8)", nullable: true),
                    outcome = table.Column<string>(type: "text", nullable: true),
                    outcome_detail = table.Column<string>(type: "jsonb", nullable: true),
                    outcome_recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runs", x => x.id);
                    table.ForeignKey(
                        name: "FK_runs_prompts_prompt_hash",
                        column: x => x.prompt_hash,
                        principalTable: "prompts",
                        principalColumn: "hash",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "run_steps",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    rung = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    prompt_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    model = table.Column<string>(type: "text", nullable: true),
                    input = table.Column<string>(type: "jsonb", nullable: true),
                    output = table.Column<string>(type: "jsonb", nullable: true),
                    confidence = table.Column<double>(type: "double precision", nullable: true),
                    stop_reason = table.Column<string>(type: "text", nullable: false),
                    input_tokens = table.Column<long>(type: "bigint", nullable: true),
                    output_tokens = table.Column<long>(type: "bigint", nullable: true),
                    cost_usd = table.Column<decimal>(type: "numeric(18,8)", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    duration_ms = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_run_steps", x => x.id);
                    table.CheckConstraint("ck_run_steps_confidence", "confidence IS NULL OR (confidence >= 0 AND confidence <= 1)");
                    table.ForeignKey(
                        name: "FK_run_steps_prompts_prompt_hash",
                        column: x => x.prompt_hash,
                        principalTable: "prompts",
                        principalColumn: "hash",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_run_steps_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_run_steps_prompt_hash",
                table: "run_steps",
                column: "prompt_hash");

            migrationBuilder.CreateIndex(
                name: "IX_run_steps_run_id_seq",
                table: "run_steps",
                columns: new[] { "run_id", "seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_runs_prompt_hash",
                table: "runs",
                column: "prompt_hash");

            migrationBuilder.CreateIndex(
                name: "IX_runs_workflow_id_workflow_run_id",
                table: "runs",
                columns: new[] { "workflow_id", "workflow_run_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_runs_workflow_type_started_at",
                table: "runs",
                columns: new[] { "workflow_type", "started_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "run_steps");

            migrationBuilder.DropTable(
                name: "runs");

            migrationBuilder.DropTable(
                name: "prompts");
        }
    }
}
