namespace AgentPlatform.Host.Livemap;

public enum LivemapGateOutcome
{
    Passed,
    Pending,
    Failed,
    MainMoved,
    Refused,
}

public sealed record LivemapGateVerdict(LivemapGateOutcome Outcome, IReadOnlyList<string> Names, string? Reason)
{
    public static LivemapGateVerdict Passed { get; } = new(LivemapGateOutcome.Passed, [], null);

    public static LivemapGateVerdict MainMoved { get; } = new(LivemapGateOutcome.MainMoved, [], "Main has moved on from the commit the pull request was made on.");

    public static LivemapGateVerdict Pending(IReadOnlyList<string> names) => new(LivemapGateOutcome.Pending, names, null);

    public static LivemapGateVerdict Failed(IReadOnlyList<string> names) => new(LivemapGateOutcome.Failed, names, null);

    public static LivemapGateVerdict Refused(string reason) => new(LivemapGateOutcome.Refused, [], reason);
}

// Everything the gate reads comes from the GitHub API for the head sha the worker committed.
public sealed record LivemapGateFacts(
    LivemapPullRequest Pull,
    string Fork,
    string Branch,
    string ExpectedHeadSha,
    string AppLogin,
    string MainHead,
    IReadOnlyList<string> HeadParents,
    IReadOnlyList<LivemapPullRequestFile> Files,
    string? HeadFileMode,
    string? MainFileMode,
    IReadOnlyList<LivemapCheckRun> CheckRuns);

public static class LivemapMergeGate
{
    public const string BuildsPath = "data/builds.json";

    public const string PlainFileMode = "100644";

    private const string MainBranch = "main";

    private const string ChecksApp = "github-actions";

    public static LivemapGateVerdict Evaluate(LivemapGateFacts facts, IReadOnlyList<string> requiredChecks)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(requiredChecks);

        if (requiredChecks.Count == 0)
        {
            return LivemapGateVerdict.Refused("No required checks are set.");
        }

        if (Refusal(facts) is { } reason)
        {
            return LivemapGateVerdict.Refused(reason);
        }

        return facts.HeadParents[0] != facts.MainHead ? LivemapGateVerdict.MainMoved : Checks(facts.CheckRuns, requiredChecks);
    }

    private static string? Refusal(LivemapGateFacts facts)
    {
        var pull = facts.Pull;
        if (pull.State != "open" || pull.Merged)
        {
            return "The pull request is not open.";
        }

        if (pull.Draft)
        {
            return "The pull request is a draft.";
        }

        if (!SameRepository(pull.HeadRepo, facts.Fork) || !SameRepository(pull.BaseRepo, facts.Fork))
        {
            return "The pull request is not between branches of the fork.";
        }

        if (pull.BaseRef != MainBranch || pull.HeadRef != facts.Branch)
        {
            return "The pull request is not the build branch against main.";
        }

        if (!string.Equals(pull.AuthorLogin, facts.AppLogin, StringComparison.OrdinalIgnoreCase) || pull.AuthorType != "Bot")
        {
            return "The pull request was not opened by the platform app.";
        }

        if (pull.HeadSha != facts.ExpectedHeadSha || pull.Commits != 1 || facts.HeadParents.Count != 1)
        {
            return "The pull request head is not the single commit the worker made.";
        }

        if (facts.Files is not [{ Filename: BuildsPath, Status: "modified", PreviousFilename: null }])
        {
            return $"The pull request does not change exactly {BuildsPath}.";
        }

        return facts.HeadFileMode == PlainFileMode && facts.MainFileMode == PlainFileMode
            ? null
            : $"{BuildsPath} is not a plain file.";
    }

    private static LivemapGateVerdict Checks(IReadOnlyList<LivemapCheckRun> runs, IReadOnlyList<string> required)
    {
        var failed = new List<string>();
        var pending = new List<string>();
        foreach (var name in required)
        {
            var own = runs.Where(run => run.Name == name && run.AppSlug == ChecksApp).ToList();
            if (own.Any(run => run.Status == "completed" && run.Conclusion != "success"))
            {
                failed.Add(name);
            }
            else if (own.Count == 0 || own.Any(run => run.Status != "completed"))
            {
                pending.Add(name);
            }
        }

        if (failed.Count > 0)
        {
            return LivemapGateVerdict.Failed(failed);
        }

        return pending.Count > 0 ? LivemapGateVerdict.Pending(pending) : LivemapGateVerdict.Passed;
    }

    private static bool SameRepository(string repository, string fork) => string.Equals(repository, fork, StringComparison.OrdinalIgnoreCase);
}
