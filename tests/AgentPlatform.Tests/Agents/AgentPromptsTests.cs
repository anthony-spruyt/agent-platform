using System.Text.RegularExpressions;
using AgentPlatform.Host.Agents;

namespace AgentPlatform.Tests.Agents;

public partial class AgentPromptsTests
{
    private const string Repo = "anthony-spruyt/widgets";
    private static readonly string Sha = new('a', 40);

    [GeneratedRegex(@"\{[a-z_]+\}")]
    private static partial Regex SingleBracePlaceholder();

    [Fact]
    public void TheRenovateTriagePrompt_HasEveryPlaceholderFilled()
    {
        var rendered = AgentPrompts.Render(AgentPrompts.Load(AgentPrompts.RenovateTriage), new AgentRepoContext(Repo, 7, Sha));

        Assert.DoesNotContain("{{", rendered, StringComparison.Ordinal);
        Assert.DoesNotMatch(SingleBracePlaceholder(), rendered);
    }

    [Fact]
    public void TheRenovateTriagePrompt_NamesTheRepositoryAndTheHeadInItsCheckRunsPath()
    {
        var rendered = AgentPrompts.Render(AgentPrompts.Load(AgentPrompts.RenovateTriage), new AgentRepoContext(Repo, 7, Sha));

        Assert.Contains($"repos/{Repo}/commits/{Sha}/check-runs", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ARunWithoutAPullRequest_FillsTheRepositoryAndTheShaOfAPromptThatNeedsNoPullRequest()
    {
        var rendered = AgentPrompts.Render("Read {{repo}} at {{head_sha}}.", new AgentRepoContext(Repo, null, Sha));

        Assert.Equal($"Read {Repo} at {Sha}.", rendered);
    }

    [Fact]
    public void ARunWithoutAPullRequest_RejectsAPromptThatNamesOne()
    {
        var failure = Assert.Throws<InvalidOperationException>(
            () => AgentPrompts.Render(AgentPrompts.Load(AgentPrompts.RenovateTriage), new AgentRepoContext(Repo, null, Sha)));

        Assert.Contains("pull request", failure.Message, StringComparison.OrdinalIgnoreCase);
    }
}
