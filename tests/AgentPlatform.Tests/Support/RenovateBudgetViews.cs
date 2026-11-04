using AgentPlatform.Host.ClaudeBudget;
using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Host.RunData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentPlatform.Tests.Support;

// Reads shared budget rows and inputs in the repo, pull request and head terms the Renovate tests speak.
internal static class RenovateBudgetViews
{
    extension(ClaudeRun run)
    {
        public string Repo => run.Scope;

        public int PrNumber => RenovateBudget.PrNumber(run.Subject);

        public string HeadSha => run.Revision!;
    }

    extension(ReserveClaudeRunInput input)
    {
        public string Repo => input.Scope;

        public int PrNumber => RenovateBudget.PrNumber(input.Subject);

        public string HeadSha => input.Revision!;

        public bool Security => input.ExemptFromGlobalCaps;
    }

    extension(RedriveCandidate candidate)
    {
        public string Repo => candidate.Scope;

        public int PrNumber => RenovateBudget.PrNumber(candidate.Subject);

        public bool Security => candidate.ExemptFromGlobalCaps;
    }
}

internal static class RenovateBudgetFixtures
{
    public static ClaudeBudgetOptions Caps(int perHour = 2, int perDay = 8, int perPr = 2) => new()
    {
        MaxPerHour = perHour,
        MaxPerDay = perDay,
        Kinds = { [RenovateBudget.Kind] = new ClaudeKindOptions { MaxPerSubjectPerDay = perPr } },
    };

    public static string Key(string cap) => cap switch
    {
        "MaxPerPrPerDay" => $"ClaudeBudget:Kinds:{RenovateBudget.Kind}:MaxPerSubjectPerDay",
        _ => $"ClaudeBudget:{cap}",
    };

    public static RenovateCaps CapsOf(IServiceProvider provider)
    {
        var options = provider.GetRequiredService<IOptions<ClaudeBudgetOptions>>().Value;
        return new RenovateCaps(options.MaxPerHour, options.MaxPerDay, options.For(RenovateBudget.Kind).MaxPerSubjectPerDay);
    }

    public static ClaudeBudgetStore Store(IDbContextFactory<RunDataDbContext> factory, TimeProvider time, ClaudeBudgetOptions? caps = null) =>
        new(factory, Options.Create(caps ?? Caps()), time, [new TriageAttemptLedger()]);
}

internal sealed record RenovateCaps(int MaxPerHour, int MaxPerDay, int MaxPerPrPerDay);
