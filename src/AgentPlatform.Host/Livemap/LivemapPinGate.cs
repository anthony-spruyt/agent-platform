namespace AgentPlatform.Host.Livemap;

// Everything the gate reads comes from the pin repository's API for the pull request's current head sha.
public sealed record LivemapPinFacts(
    LivemapPullRequest Pull,
    string PinRepo,
    string PinPath,
    string Branch,
    string ExpectedHeadSha,
    string AppLogin,
    string MainHead,
    IReadOnlyList<string> HeadParents,
    IReadOnlyList<LivemapPullRequestFile> Files);

public static class LivemapPinGate
{
    private const string MainBranch = "main";

    public static LivemapGateVerdict Evaluate(LivemapPinFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (Refusal(facts) is { } reason)
        {
            return LivemapGateVerdict.Refused(reason);
        }

        return facts.HeadParents[0] != facts.MainHead ? LivemapGateVerdict.MainMoved : LivemapGateVerdict.Passed;
    }

    private static string? Refusal(LivemapPinFacts facts)
    {
        var pull = facts.Pull;
        if (pull.State != "open" || pull.Merged)
        {
            return "The pin pull request is not open.";
        }

        if (pull.Draft)
        {
            return "The pin pull request is a draft.";
        }

        if (!string.Equals(pull.HeadRepo, facts.PinRepo, StringComparison.OrdinalIgnoreCase) || !string.Equals(pull.BaseRepo, facts.PinRepo, StringComparison.OrdinalIgnoreCase))
        {
            return "The pin pull request is not between branches of the pin repository.";
        }

        if (pull.BaseRef != MainBranch || pull.HeadRef != facts.Branch)
        {
            return "The pin pull request is not the worker's pin branch against main.";
        }

        if (!string.Equals(pull.AuthorLogin, facts.AppLogin, StringComparison.OrdinalIgnoreCase) || pull.AuthorType != "Bot")
        {
            return "The pin pull request was not opened by the worker.";
        }

        if (pull.Commits != 1 || facts.HeadParents.Count != 1 || pull.HeadSha != facts.ExpectedHeadSha)
        {
            return "The pin pull request is not the single commit the worker made.";
        }

        return facts.Files is [{ Status: "modified", PreviousFilename: null } file] && file.Filename == facts.PinPath
            ? null
            : "The pin pull request changes more than the pin file.";
    }
}
