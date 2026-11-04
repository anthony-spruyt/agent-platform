using AgentPlatform.Host.ClaudeBudget;
using AgentPlatform.Host.RenovateTriage;

namespace AgentPlatform.IntegrationTests;

// Reads shared budget rows in the repo, pull request and head terms the Renovate tests speak.
internal static class RenovateBudgetViews
{
    extension(ClaudeRun run)
    {
        public string Repo => run.Scope;

        public int PrNumber => RenovateBudget.PrNumber(run.Subject);

        public string HeadSha => run.Revision!;
    }
}
