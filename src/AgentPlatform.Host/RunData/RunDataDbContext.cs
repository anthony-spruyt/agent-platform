using System.Text;
using AgentPlatform.Host.RenovateTriage;
using Microsoft.EntityFrameworkCore;

namespace AgentPlatform.Host.RunData;

public sealed class RunDataDbContext(DbContextOptions<RunDataDbContext> options) : DbContext(options)
{
    private const string Jsonb = "jsonb";
    private const string Money = "numeric(18,8)";

    public DbSet<Prompt> Prompts => Set<Prompt>();

    public DbSet<Run> Runs => Set<Run>();

    public DbSet<RunStep> RunSteps => Set<RunStep>();

    public DbSet<RenovateTriageVerdict> TriageVerdicts => Set<RenovateTriageVerdict>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<RenovateTriageVerdict>(verdict =>
        {
            verdict.ToTable("renovate_triage_verdicts");
            verdict.HasKey(v => v.Id);
            verdict.Property(v => v.Id).ValueGeneratedNever();
            verdict.Property(v => v.Repo).HasMaxLength(200);
            verdict.Property(v => v.HeadSha).HasMaxLength(64);
            verdict.Property(v => v.Verdict).HasConversion<string>().HasMaxLength(16);
            verdict.Property(v => v.Mode).HasConversion<string>().HasMaxLength(16);
            verdict.Property(v => v.Reasons).HasColumnType(Jsonb);
            verdict.Property(v => v.CreatedAt).HasDefaultValueSql("now()");
            verdict.HasIndex(v => new { v.Repo, v.PrNumber, v.HeadSha }).IsUnique();
        });

        modelBuilder.Entity<Prompt>(prompt =>
        {
            prompt.ToTable("prompts");
            prompt.HasKey(p => p.Hash);
            prompt.Property(p => p.Hash).HasMaxLength(64);
            prompt.Property(p => p.CreatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<Run>(run =>
        {
            run.ToTable("runs");
            run.HasKey(r => r.Id);
            run.Property(r => r.Id).ValueGeneratedNever();
            run.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
            run.Property(r => r.InputEvent).HasColumnType(Jsonb);
            run.Property(r => r.Context).HasColumnType(Jsonb);
            run.Property(r => r.OutcomeDetail).HasColumnType(Jsonb);
            run.Property(r => r.CostUsd).HasColumnType(Money);
            run.Property(r => r.PromptHash).HasMaxLength(64);
            run.HasIndex(r => new { r.WorkflowId, r.WorkflowRunId }).IsUnique();
            run.HasIndex(r => new { r.WorkflowType, r.StartedAt });
            run.HasOne<Prompt>().WithMany().HasForeignKey(r => r.PromptHash).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RunStep>(step =>
        {
            step.ToTable("run_steps", table => table.HasCheckConstraint("ck_run_steps_confidence", "confidence IS NULL OR (confidence >= 0 AND confidence <= 1)"));
            step.HasKey(s => s.Id);
            step.Property(s => s.Rung).HasConversion<string>().HasMaxLength(16);
            step.Property(s => s.Input).HasColumnType(Jsonb);
            step.Property(s => s.Output).HasColumnType(Jsonb);
            step.Property(s => s.CostUsd).HasColumnType(Money);
            step.Property(s => s.PromptHash).HasMaxLength(64);
            step.HasIndex(s => new { s.RunId, s.Seq }).IsUnique();
            step.HasOne<Run>().WithMany(r => r.Steps).HasForeignKey(s => s.RunId).OnDelete(DeleteBehavior.Cascade);
            step.HasOne<Prompt>().WithMany().HasForeignKey(s => s.PromptHash).OnDelete(DeleteBehavior.Restrict);
        });

        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(e => e.GetProperties()))
        {
            property.SetColumnName(ToSnakeCase(property.Name));
        }
    }

    private static string ToSnakeCase(string name)
    {
        var snake = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
            {
                snake.Append('_');
            }

            snake.Append(char.ToLowerInvariant(name[i]));
        }

        return snake.ToString();
    }
}
