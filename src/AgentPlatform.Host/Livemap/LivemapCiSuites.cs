namespace AgentPlatform.Host.Livemap;

internal static class LivemapCiSuites
{
    // A required name only counts in the check suite of the fork's own CI run for the branch.
    public static async Task<IReadOnlySet<long>> FindAsync(
        ILivemapGitHub github,
        IReadOnlyList<LivemapCheckRun> checkRuns,
        string branch,
        IReadOnlyList<string> required,
        CancellationToken cancellationToken)
    {
        var suites = new HashSet<long>();
        var candidates = checkRuns
            .Where(run => run.AppSlug == LivemapMergeGate.ChecksApp && run.CheckSuiteId > 0 && required.Contains(run.Name))
            .Select(run => run.CheckSuiteId)
            .Distinct();
        foreach (var suite in candidates)
        {
            var runs = await github.ListSuiteRunsAsync(suite, cancellationToken).ConfigureAwait(false);
            if (runs.Count > 0 && runs.All(run => run.IsCiRunOf(branch)))
            {
                suites.Add(suite);
            }
        }

        return suites;
    }
}
