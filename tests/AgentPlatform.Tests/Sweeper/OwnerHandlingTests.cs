using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.RenovateTriage;

namespace AgentPlatform.Tests.Sweeper;

public class OwnerHandlingTests
{
    private const string Owner = "anthony-spruyt";
    private static readonly DateTimeOffset Since = new(2026, 10, 15, 10, 0, 0, TimeSpan.Zero);

    private static GitHubReview Review(string? login, int minutesAfter, string state = "COMMENTED") =>
        new(1, login, "OWNER", state, "sha", Since.AddMinutes(minutesAfter));

    private static GitHubIssueEvent Event(string name, string? actor, string? label, int minutesAfter) =>
        new(name, actor, label, Since.AddMinutes(minutesAfter));

    private static bool Handled(IReadOnlyList<GitHubReview>? reviews = null, IReadOnlyList<GitHubIssueEvent>? events = null) =>
        OwnerHandling.IsHandled(Owner, Since, reviews ?? [], events ?? []);

    [Theory]
    [InlineData("APPROVED")]
    [InlineData("COMMENTED")]
    [InlineData("CHANGES_REQUESTED")]
    public void AReviewByTheOwnerAfterTheQueueing_IsHandled(string state)
    {
        Assert.True(Handled(reviews: [Review(Owner, 5, state)]));
    }

    [Fact]
    public void TheOwnerLogin_IsMatchedWithoutCase()
    {
        Assert.True(OwnerHandling.IsHandled("Anthony-Spruyt", Since, [Review(Owner, 5)], []));
    }

    [Fact]
    public void AReviewByAnyoneElse_OrBeforeTheQueueing_OrNotYetSubmitted_IsNotHandled()
    {
        Assert.False(Handled(reviews: [Review("skynet-rw[bot]", 5), Review(Owner, -5), new GitHubReview(2, Owner, "OWNER", "PENDING", "sha", null)]));
    }

    [Theory]
    [InlineData("unlabeled", TriageRules.NeedsReviewLabel)]
    [InlineData("unlabeled", TriageRules.NeedsTriageLabel)]
    [InlineData("labeled", TriageRules.BlockedLabel)]
    [InlineData("labeled", TriageRules.RevertLabel)]
    public void AnOwnerLabelChangeThatTakesThePrOverTheQueue_IsHandled(string name, string label)
    {
        Assert.True(Handled(events: [Event(name, Owner, label, 5)]));
    }

    [Theory]
    [InlineData("labeled", TriageRules.NeedsTriageLabel)]
    [InlineData("labeled", TriageRules.NeedsReviewLabel)]
    [InlineData("unlabeled", TriageRules.BlockedLabel)]
    [InlineData("unlabeled", "dependencies")]
    [InlineData("closed", null)]
    public void OtherOwnerLabelEvents_AreNotHandled(string name, string? label)
    {
        Assert.False(Handled(events: [Event(name, Owner, label, 5)]));
    }

    [Fact]
    public void LabelEventsByTheAppOrOlderThanTheQueueing_AreNotHandled()
    {
        Assert.False(Handled(events: [Event("unlabeled", "skynet-rw[bot]", TriageRules.NeedsTriageLabel, 5), Event("unlabeled", Owner, TriageRules.NeedsTriageLabel, -5), Event("unlabeled", null, TriageRules.NeedsTriageLabel, 5)]));
    }
}
