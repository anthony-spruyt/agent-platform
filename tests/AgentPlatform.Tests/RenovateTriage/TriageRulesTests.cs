using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.RenovateTriage;

namespace AgentPlatform.Tests.RenovateTriage;

public class TriageRulesTests
{
    private static GitHubCheckRun Run(string name, string status = "completed", string? conclusion = "success", string app = "github-actions") =>
        new(1, name, status, conclusion, app);

    private const string Head = "0123456789abcdef0123456789abcdef01234567";

    private static GitHubReview Review(string? user, string state, string? association = "COLLABORATOR", string? commit = Head) =>
        new(1, user, association, state, commit, null);

    private static JevAnswers Jev(string bump, double breaking = 0.04, double changelog = 0.78) =>
        new(breaking, bump, changelog);

    private static GitHubCheckSuite Suite(string app = "github-actions", string status = "completed", int runs = 1) =>
        new(1, status, status == "completed" ? "success" : null, app, runs);

    private static TriageDecision Decide(string branch, JevAnswers jev, BumpFacts? facts = null) => TriageRules.DecideFromJev(branch, jev, facts ?? new BumpFacts(true, []));

    private static BumpFacts Versions(params (string Old, string New, string Kind)[] changes) =>
        new(false, [.. changes.Select(c => new VersionChange(c.Old, c.New, c.Kind))]);

    public class AuthorAndLabels
    {
        [Fact]
        public void RenovateWithNoBlockingLabels_IsEligible()
        {
            Assert.Null(TriageRules.SkipReason(29139614, ["dep/minor", "renovate/image"]));
        }

        [Theory]
        [InlineData(null)]
        [InlineData(0L)]
        [InlineData(99536297L)]
        [InlineData(29139615L)]
        public void AnyoneButRenovate_IsSkipped(long? authorId)
        {
            Assert.Equal(TriageRules.NotRenovate, TriageRules.SkipReason(authorId, []));
        }

        [Theory]
        [InlineData("agent/needs-human-review")]
        [InlineData("agent/revert")]
        [InlineData("blocked")]
        public void ABlockingLabel_IsSkipped(string label)
        {
            Assert.Equal(TriageRules.BlockingLabel, TriageRules.SkipReason(29139614, ["dep/minor", label]));
        }

        [Theory]
        [InlineData("agent/needs-human-review")]
        [InlineData("agent/auto-approved")]
        public void InShadowMode_TheLabelsOfTheLivePath_AreIgnored(string label)
        {
            Assert.Null(TriageRules.SkipReason(29139614, [label], ignoreTriageSignals: true));
        }

        [Fact]
        public void TheNeedsReviewLabel_IsTheNewName()
        {
            Assert.Equal("agent/needs-human-review", TriageRules.NeedsReviewLabel);
        }

        [Fact]
        public void TheNeedsReviewLabel_StillBlocksWhenAskedToHonourIt()
        {
            Assert.Equal(TriageRules.BlockingLabel, TriageRules.SkipReason(29139614, ["agent/needs-human-review"], ignoreTriageSignals: true, honourNeedsReviewLabel: true));
        }

        [Fact]
        public void TheAutoApprovedLabel_DoesNotBlockWhenAskedToHonourTheNeedsReviewLabel()
        {
            Assert.Null(TriageRules.SkipReason(29139614, ["agent/auto-approved"], ignoreTriageSignals: true, honourNeedsReviewLabel: true));
        }

        [Theory]
        [InlineData("agent/revert")]
        [InlineData("blocked")]
        public void InShadowMode_OtherBlockingLabels_StillSkip(string label)
        {
            Assert.Equal(TriageRules.BlockingLabel, TriageRules.SkipReason(29139614, ["agent/needs-human-review", label], ignoreTriageSignals: true));
        }

        [Theory]
        [InlineData("agent/needs-human-review")]
        [InlineData("agent/revert")]
        [InlineData("blocked")]
        public void OutsideShadowMode_EveryBlockingLabel_Skips(string label)
        {
            Assert.Equal(TriageRules.BlockingLabel, TriageRules.SkipReason(29139614, [label], ignoreTriageSignals: false));
        }

        [Fact]
        public void TheOldNeedsReviewName_IsJustAnotherLabel()
        {
            Assert.Null(TriageRules.SkipReason(29139614, ["agent/needs-review"], ignoreTriageSignals: false));
            Assert.Null(TriageRules.SkipReason(29139614, ["agent/needs-review"], ignoreTriageSignals: true, honourNeedsReviewLabel: true));
        }

        [Theory]
        [InlineData("agent/needs-triage", false, true)]
        [InlineData("agent/needs-triage", true, false)]
        [InlineData("agent/needs-human-review", false, true)]
        [InlineData("agent/needs-human-review", true, true)]
        [InlineData("agent/revert", true, true)]
        [InlineData("blocked", true, true)]
        [InlineData("agent/safe", false, false)]
        [InlineData("agent/fixable", true, false)]
        [InlineData("agent/auto-approved", false, false)]
        public void IsBlocking_LetsAPendingRowOverrideOnlyTheNeedsTriageLabel(string label, bool hasPendingRow, bool expected)
        {
            Assert.Equal(expected, TriageRules.IsBlocking(label, hasPendingRow));
        }

        [Fact]
        public void TheNeedsTriageLabel_BlocksWithoutAPendingRow_SoAHandAddedLabelNeverStartsARun()
        {
            Assert.Equal(TriageRules.BlockingLabel, TriageRules.SkipReason(29139614, ["agent/needs-triage"]));
        }

        [Fact]
        public void TheNeedsTriageLabel_DoesNotBlockWhileAPendingRowExists()
        {
            Assert.Null(TriageRules.SkipReason(29139614, ["agent/needs-triage"], hasPendingRow: true));
        }

        [Fact]
        public void APendingRow_NeverIgnoresTheNeedsReviewLabelOrTheOwnersLabels()
        {
            Assert.Equal(TriageRules.BlockingLabel, TriageRules.SkipReason(29139614, ["agent/needs-human-review"], hasPendingRow: true));
            Assert.Equal(TriageRules.BlockingLabel, TriageRules.SkipReason(29139614, ["agent/needs-triage", "blocked"], hasPendingRow: true));
            Assert.Equal(TriageRules.BlockingLabel, TriageRules.SkipReason(29139614, ["agent/needs-triage", "agent/revert"], hasPendingRow: true));
        }

        [Fact]
        public void InShadowMode_TheNeedsTriageLabelIsIgnored()
        {
            Assert.Null(TriageRules.SkipReason(29139614, ["agent/needs-triage"], ignoreTriageSignals: true));
        }

        [Theory]
        [InlineData(TriageVerdict.Approve, true)]
        [InlineData(TriageVerdict.Safe, true)]
        [InlineData(TriageVerdict.NeedsReview, false)]
        [InlineData(TriageVerdict.Fixable, false)]
        [InlineData(TriageVerdict.Blocked, false)]
        [InlineData(TriageVerdict.Owner, false)]
        public void OnlyApprovingVerdictsAreRefusedByTheNeedsReviewLabel(TriageVerdict verdict, bool expected)
        {
            Assert.Equal(expected, TriageRules.IsApproving(verdict));
        }

        [Fact]
        public void TheAuthorIsCheckedBeforeTheLabels()
        {
            Assert.Equal(TriageRules.NotRenovate, TriageRules.SkipReason(1, ["blocked"]));
        }

        [Theory]
        [InlineData("Blocked")]
        [InlineData("agent/auto-approved")]
        public void OtherLabels_AreNotBlocking(string label)
        {
            Assert.Null(TriageRules.SkipReason(29139614, [label]));
        }
    }

    public class ReviewState
    {
        [Fact]
        public void NoReviews_IsNotApproved()
        {
            Assert.False(TriageRules.IsApproved([], Head));
        }

        [Fact]
        public void AnApproval_IsApproved()
        {
            Assert.True(TriageRules.IsApproved([Review("alice", "APPROVED")], Head));
        }

        [Fact]
        public void CommentsDoNotChangeAnApproval()
        {
            Assert.True(TriageRules.IsApproved([Review("alice", "APPROVED"), Review("alice", "COMMENTED")], Head));
        }

        [Fact]
        public void OnlyComments_IsNotApproved()
        {
            Assert.False(TriageRules.IsApproved([Review("alice", "COMMENTED")], Head));
        }

        [Fact]
        public void TheLatestNonCommentReviewOfAUserWins()
        {
            Assert.False(TriageRules.IsApproved([Review("alice", "APPROVED"), Review("alice", "CHANGES_REQUESTED")], Head));
            Assert.True(TriageRules.IsApproved([Review("alice", "CHANGES_REQUESTED"), Review("alice", "APPROVED")], Head));
        }

        [Fact]
        public void ADismissedReview_ReplacesTheEarlierApproval()
        {
            Assert.False(TriageRules.IsApproved([Review("alice", "APPROVED"), Review("alice", "DISMISSED")], Head));
        }

        [Fact]
        public void ChangesRequestedByAnyTrustedReviewerBeatsApprovals()
        {
            Assert.False(TriageRules.IsApproved([Review("alice", "APPROVED"), Review("bob", "CHANGES_REQUESTED"), Review("carol", "APPROVED")], Head));
        }

        [Fact]
        public void ReviewsWithoutAnAuthor_AreGroupedTogether()
        {
            Assert.False(TriageRules.IsApproved([Review(null, "APPROVED"), Review(null, "CHANGES_REQUESTED")], Head));
        }

        [Theory]
        [InlineData("OWNER")]
        [InlineData("MEMBER")]
        [InlineData("COLLABORATOR")]
        public void ApprovalsFromRepositoryOwnersMembersAndCollaborators_Count(string association)
        {
            Assert.True(TriageRules.IsApproved([Review("alice", "APPROVED", association)], Head));
        }

        [Theory]
        [InlineData("CONTRIBUTOR")]
        [InlineData("FIRST_TIME_CONTRIBUTOR")]
        [InlineData("FIRST_TIMER")]
        [InlineData("MANNEQUIN")]
        [InlineData("NONE")]
        [InlineData("collaborator")]
        [InlineData("")]
        [InlineData(null)]
        public void ApprovalsFromEveryoneElse_DoNotCount(string? association)
        {
            Assert.False(TriageRules.IsApproved([Review("alice", "APPROVED", association)], Head));
        }

        [Fact]
        public void TheTriageAppBot_CountsWhateverItsAssociationIs()
        {
            Assert.True(TriageRules.IsApproved([Review("skynet-rw[bot]", "APPROVED", "CONTRIBUTOR")], Head));
            Assert.True(TriageRules.IsApproved([Review(TriageRules.TriageBotLogin, "APPROVED", "NONE")], Head));
        }

        [Fact]
        public void InShadowMode_TheTriageAppBot_IsIgnored()
        {
            Assert.False(TriageRules.IsApproved([Review(TriageRules.TriageBotLogin, "APPROVED", "CONTRIBUTOR")], Head, ignoreTriageSignals: true));
        }

        [Fact]
        public void InShadowMode_AHumanApproval_StillCounts()
        {
            Assert.True(TriageRules.IsApproved([Review(TriageRules.TriageBotLogin, "APPROVED", "CONTRIBUTOR"), Review("alice", "APPROVED")], Head, ignoreTriageSignals: true));
        }

        [Fact]
        public void InShadowMode_TheTriageAppBotRequestingChanges_IsIgnored()
        {
            Assert.True(TriageRules.IsApproved([Review("alice", "APPROVED"), Review(TriageRules.TriageBotLogin, "CHANGES_REQUESTED", "CONTRIBUTOR")], Head, ignoreTriageSignals: true));
        }

        [Fact]
        public void OutsideShadowMode_TheTriageAppBot_Counts()
        {
            Assert.True(TriageRules.IsApproved([Review(TriageRules.TriageBotLogin, "APPROVED", "CONTRIBUTOR")], Head, ignoreTriageSignals: false));
        }

        [Theory]
        [InlineData("skynet-r[bot]")]
        [InlineData("skynet-rw")]
        [InlineData("Skynet-rw[bot]")]
        [InlineData("renovate[bot]")]
        public void OtherLoginsLookingLikeTheBot_DoNotCount(string login)
        {
            Assert.False(TriageRules.IsApproved([Review(login, "APPROVED", "CONTRIBUTOR")], Head));
        }

        [Theory]
        [InlineData("fedcba9876543210fedcba9876543210fedcba98")]
        [InlineData(null)]
        [InlineData("")]
        public void AnApprovalOfAnotherCommit_DoesNotCount(string? commit)
        {
            Assert.False(TriageRules.IsApproved([Review("alice", "APPROVED", commit: commit)], Head));
        }

        [Fact]
        public void AnEmptyHead_NeverMatchesAnApproval()
        {
            Assert.False(TriageRules.IsApproved([Review("alice", "APPROVED", commit: string.Empty)], string.Empty));
        }

        [Fact]
        public void AStaleLatestApproval_DoesNotFallBackToAnEarlierCurrentOne()
        {
            Assert.False(TriageRules.IsApproved([Review("alice", "APPROVED"), Review("alice", "APPROVED", commit: "fedcba9876543210fedcba9876543210fedcba98")], Head));
        }

        [Fact]
        public void ChangesRequestedFromAnUntrustedReviewer_IsIgnored()
        {
            Assert.True(TriageRules.IsApproved([Review("alice", "APPROVED"), Review("mallory", "CHANGES_REQUESTED", "NONE")], Head));
        }

        [Fact]
        public void ChangesRequestedFromATrustedReviewer_CountsOnAnyCommit()
        {
            Assert.False(TriageRules.IsApproved([Review("alice", "APPROVED"), Review("bob", "CHANGES_REQUESTED", commit: "fedcba9876543210fedcba9876543210fedcba98")], Head));
        }
    }

    public class CommitGate
    {
        private static GitHubCommit Commit(
            string? login = "renovate[bot]",
            long? id = 29139614,
            string? authorEmail = "29139614+renovate[bot]@users.noreply.github.com",
            string? committerEmail = "noreply@github.com",
            bool verified = true) =>
            new("abc", login, id, authorEmail, committerEmail, verified);

        [Fact]
        public void VerifiedCommitsAuthoredByRenovateAndCommittedByGitHub_Pass()
        {
            Assert.True(TriageRules.AreAllRenovateCommits([Commit(), Commit()], 2));
        }

        [Fact]
        public void NoCommits_Fail()
        {
            Assert.False(TriageRules.AreAllRenovateCommits([], 0));
        }

        [Theory]
        [InlineData(1, 2)]
        [InlineData(2, 1)]
        [InlineData(2, 0)]
        public void AListThatDiffersFromThePrCommitCount_Fails(int listed, int expected)
        {
            Assert.False(TriageRules.AreAllRenovateCommits([.. Enumerable.Repeat(Commit(), listed)], expected));
        }

        [Theory]
        [InlineData(249, true)]
        [InlineData(250, false)]
        [InlineData(251, false)]
        public void ACountAtTheListingLimit_Fails(int count, bool expected)
        {
            Assert.Equal(expected, TriageRules.AreAllRenovateCommits([.. Enumerable.Repeat(Commit(), Math.Min(count, TriageRules.MaxListedCommits))], count));
        }

        [Fact]
        public void OneBadCommitAmongGoodOnes_Fails()
        {
            Assert.False(TriageRules.AreAllRenovateCommits([Commit(), Commit(login: "alice"), Commit()], 3));
        }

        [Theory]
        [InlineData("alice", 29139614L, "29139614+renovate[bot]@users.noreply.github.com", "noreply@github.com", true)]
        [InlineData(null, 29139614L, "29139614+renovate[bot]@users.noreply.github.com", "noreply@github.com", true)]
        [InlineData("renovate[bot]", 1L, "29139614+renovate[bot]@users.noreply.github.com", "noreply@github.com", true)]
        [InlineData("renovate[bot]", null, "29139614+renovate[bot]@users.noreply.github.com", "noreply@github.com", true)]
        [InlineData("renovate[bot]", 29139614L, "alice@example.test", "noreply@github.com", true)]
        [InlineData("renovate[bot]", 29139614L, null, "noreply@github.com", true)]
        [InlineData("renovate[bot]", 29139614L, "29139614+renovate[bot]@users.noreply.github.com", "alice@example.test", true)]
        [InlineData("renovate[bot]", 29139614L, "29139614+renovate[bot]@users.noreply.github.com", null, true)]
        [InlineData("renovate[bot]", 29139614L, "29139614+renovate[bot]@users.noreply.github.com", "noreply@github.com", false)]
        [InlineData("Renovate[bot]", 29139614L, "29139614+renovate[bot]@users.noreply.github.com", "noreply@github.com", true)]
        public void AnythingOtherThanAVerifiedRenovateCommit_Fails(string? login, long? id, string? authorEmail, string? committerEmail, bool verified)
        {
            Assert.False(TriageRules.AreAllRenovateCommits([Commit(login, id, authorEmail, committerEmail, verified)], 1));
        }

        [Fact]
        public void TheDecision_NeedsReviewWithTheCommitComment()
        {
            Assert.Equal(
                new TriageDecision(TriageVerdict.NeedsReview, "non-renovate-commit", "**Needs review:** this PR has commits that Renovate didn't make."),
                TriageRules.NonRenovateCommitDecision());
        }
    }

    public class ChecksAssessment
    {
        private static CiState Assess(GitHubCheckRun[] runs, params GitHubCheckSuite[] suites) =>
            TriageRules.AssessChecks(runs, suites);

        [Fact]
        public void NoChecks_ArePending()
        {
            Assert.Equal(CiState.Pending, Assess([]));
        }

        [Fact]
        public void OnlyIgnoredChecks_ArePending()
        {
            Assert.Equal(CiState.Pending, Assess([Run("Mergify Merge Protections"), Run("renovate/stability-days"), Run("Summary", app: "mergify")]));
        }

        [Theory]
        [InlineData("success")]
        [InlineData("neutral")]
        [InlineData("skipped")]
        public void CompletedChecksWithAPassingConclusion_Pass(string conclusion)
        {
            Assert.Equal(CiState.Passed, Assess([Run("build", conclusion: conclusion), Run("test")]));
        }

        [Theory]
        [InlineData("failure")]
        [InlineData("cancelled")]
        [InlineData("timed_out")]
        [InlineData("action_required")]
        [InlineData("stale")]
        [InlineData("startup_failure")]
        [InlineData(null)]
        public void CompletedChecksWithAnyOtherConclusion_Fail(string? conclusion)
        {
            Assert.Equal(CiState.Failed, Assess([Run("test"), Run("build", conclusion: conclusion)]));
        }

        [Theory]
        [InlineData("queued")]
        [InlineData("in_progress")]
        [InlineData("waiting")]
        [InlineData("pending")]
        [InlineData("requested")]
        public void ACheckThatHasNotCompleted_IsPending(string status)
        {
            Assert.Equal(CiState.Pending, Assess([Run("test"), Run("build", status, null)]));
        }

        [Fact]
        public void PendingWinsOverFailed()
        {
            Assert.Equal(CiState.Pending, Assess([Run("test", conclusion: "failure"), Run("build", "in_progress", null)]));
        }

        [Theory]
        [InlineData("renovate/stability-days")]
        [InlineData("Mergify Merge Protections")]
        [InlineData("Mergify")]
        public void ChecksIgnoredEverywhere_NeverBlockOrFail(string name)
        {
            Assert.Equal(CiState.Passed, Assess([Run("test"), Run(name, "in_progress", null)]));
            Assert.Equal(CiState.Passed, Assess([Run("test"), Run(name, conclusion: "failure")]));
        }

        [Fact]
        public void EveryCheckOfTheMergifyApp_NeverBlocksOrFails()
        {
            Assert.Equal(CiState.Passed, Assess([Run("test"), Run("Summary", "in_progress", null, "mergify")]));
            Assert.Equal(CiState.Passed, Assess([Run("test"), Run("Summary", conclusion: "failure", app: "mergify")]));
        }

        [Fact]
        public void SkynetChecks_DoNotFailButStillHoldTheJudgementWhilePending()
        {
            Assert.Equal(CiState.Passed, Assess([Run("test"), Run("skynet/triage", conclusion: "failure")]));
            Assert.Equal(CiState.Pending, Assess([Run("test"), Run("skynet/triage", "in_progress", null)]));
        }

        [Fact]
        public void NamesThatMerelyContainAnIgnoredName_AreStillJudged()
        {
            Assert.Equal(CiState.Failed, Assess([Run("not-Mergify", conclusion: "failure")]));
            Assert.Equal(CiState.Failed, Assess([Run("ci/skynet/x", conclusion: "failure")]));
        }

        [Theory]
        [InlineData("queued")]
        [InlineData("in_progress")]
        public void AGitHubActionsSuiteThatHasNotCompleted_IsPendingEvenWithoutRuns(string status)
        {
            Assert.Equal(CiState.Pending, Assess([Run("build")], Suite(status: status, runs: 0)));
        }

        [Fact]
        public void AnotherAppsSuiteThatHasNotCompleted_IsPendingOnlyWhenItHasRuns()
        {
            Assert.Equal(CiState.Pending, Assess([Run("build")], Suite("sonarqubecloud", "in_progress", 1)));
            Assert.Equal(CiState.Passed, Assess([Run("build")], Suite("renovate", "queued", 0), Suite("claude", "queued", 0)));
        }

        [Fact]
        public void AMergifySuiteThatHasNotCompleted_IsIgnored()
        {
            Assert.Equal(CiState.Passed, Assess([Run("build")], Suite("mergify", "in_progress", 3)));
        }

        [Fact]
        public void CompletedSuites_NeverFailOnTheirOwn()
        {
            var failedSuite = new GitHubCheckSuite(1, "completed", "failure", "github-actions", 1);

            Assert.Equal(CiState.Passed, Assess([Run("build")], failedSuite));
        }

        [Fact]
        public void ASuiteAloneWithNoRuns_IsPending()
        {
            Assert.Equal(CiState.Pending, Assess([], Suite()));
        }
    }

    public class RiskyBranches
    {
        [Theory]
        [InlineData("renovate/kubernetes-sigs-foo")]
        [InlineData("renovate/k8s-thing")]
        [InlineData("renovate/n8n")]
        [InlineData("renovate/talos-1.x")]
        [InlineData("renovate/ceph-v19")]
        [InlineData("renovate/rook-ceph")]
        [InlineData("renovate/traefik")]
        [InlineData("renovate/cilium")]
        [InlineData("renovate/victoria-metrics")]
        [InlineData("renovate/velero")]
        [InlineData("renovate/postgres-16")]
        [InlineData("renovate/litellm")]
        [InlineData("renovate/fluxcd")]
        [InlineData("renovate/technitium")]
        [InlineData("renovate/nexus")]
        [InlineData("renovate/barman-cloud")]
        [InlineData("renovate/app-template-4.x")]
        [InlineData("renovate/cnpg")]
        [InlineData("renovate/cloudflared")]
        [InlineData("renovate/vaultwarden")]
        [InlineData("renovate/authentik")]
        [InlineData("renovate/external-dns")]
        [InlineData("renovate/foundryvtt")]
        [InlineData("renovate/KUBE-state")]
        public void BranchesNamingASensitiveComponent_AreRisky(string branch)
        {
            Assert.True(TriageRules.IsRiskyBranch(branch));
        }

        [Theory]
        [InlineData("renovate/n8n-mcp")]
        [InlineData("renovate/N8N-MCP-1.x")]
        [InlineData("renovate/czkawka")]
        [InlineData("renovate/docker-build-push-action-digest")]
        public void OtherBranches_AreNotRisky(string branch)
        {
            Assert.False(TriageRules.IsRiskyBranch(branch));
        }
    }

    public class JevDecisions
    {
        [Fact]
        public void Breaking_IsFlaggedAtTheThreshold()
        {
            var decision = Decide("renovate/foo", Jev("patch", breaking: 0.66));

            Assert.Equal(new TriageDecision(TriageVerdict.NeedsReview, TriageRules.Breaking, "**Needs review:** likely breaking changes (Jev score 0.66)."), decision);
        }

        [Fact]
        public void JustUnderTheBreakingThreshold_IsNotBreaking()
        {
            var decision = Decide("renovate/foo", Jev("digest", breaking: 0.659));

            Assert.Equal(TriageVerdict.Approve, decision.Verdict);
        }

        [Fact]
        public void BreakingBeatsADigestBump()
        {
            Assert.Equal(TriageVerdict.NeedsReview, Decide("renovate/foo", Jev("digest", breaking: 0.9)).Verdict);
        }

        [Fact]
        public void Digest_IsApprovedEvenWithoutAChangelogOrAnAllowlistedBranch()
        {
            var decision = Decide("renovate/foo", Jev("digest", changelog: 0));

            Assert.Equal(new TriageDecision(TriageVerdict.Approve, TriageRules.Digest, "**Auto-approved:** digest bump."), decision);
        }

        [Fact]
        public void Major_NeedsReviewAndNamesTheVersionsWhenTheyWereParsed()
        {
            var decision = Decide("renovate/pin-dependencies", Jev("major"), Versions(("1.9.0", "2.0.0", "major")));

            Assert.Equal(new TriageDecision(TriageVerdict.NeedsReview, TriageRules.MajorBump, "**Needs review:** major bump, `1.9.0` -> `2.0.0`."), decision);
        }

        [Fact]
        public void Major_NamesTheMajorPairWhenSeveralWereParsed()
        {
            var decision = Decide("renovate/foo", Jev("major"), Versions(("1.0.0", "1.0.1", "patch"), ("1.9.0", "2.0.0", "major")));

            Assert.Equal("**Needs review:** major bump, `1.9.0` -> `2.0.0`.", decision.Comment);
        }

        [Fact]
        public void Major_OmitsTheVersionsWhenNoneWereParsed()
        {
            var decision = Decide("renovate/foo", Jev("major"), new BumpFacts(false, []));

            Assert.Equal(new TriageDecision(TriageVerdict.NeedsReview, TriageRules.MajorBump, "**Needs review:** major bump."), decision);
        }

        [Fact]
        public void Major_OmitsTheVersionsWhenOnlyAnUnrelatedPairWasParsed()
        {
            var decision = Decide("renovate/foo", Jev("major"), Versions(("1.0.0", "1.0.1", "patch"), ("1.0.0", "1.1.0", "minor")));

            Assert.Equal("**Needs review:** major bump.", decision.Comment);
        }

        [Fact]
        public void Major_DoesNotNameACalendarPair()
        {
            var decision = Decide("renovate/foo", Jev("major"), Versions(("2024.10", "2025.1", "major")));

            Assert.Equal("**Needs review:** major bump.", decision.Comment);
        }

        [Fact]
        public void Calver_OmitsTheVersionsWhenOnlySemverPairsWereParsed()
        {
            var decision = Decide("renovate/foo", Jev("calver"), Versions(("1.9.0", "2.0.0", "major"), ("1.0.0", "1.0.1", "patch")));

            Assert.Equal("**Needs review:** calendar-versioned bump. The version can't show whether it breaks anything.", decision.Comment);
        }

        [Fact]
        public void Calver_NamesTheCalendarPairAmongSeveral()
        {
            var decision = Decide("renovate/foo", Jev("calver"), Versions(("1.9.0", "2.0.0", "major"), ("2025.1.2", "2025.2.0", "minor")));

            Assert.Equal("**Needs review:** calendar-versioned bump, `2025.1.2` -> `2025.2.0`. The version can't show whether it breaks anything.", decision.Comment);
        }

        [Fact]
        public void Calver_NeedsReviewAndNamesTheVersionsWhenTheyWereParsed()
        {
            var decision = Decide("renovate/foo", Jev("calver"), Versions(("2024.10", "2025.1", "major")));

            Assert.Equal(
                new TriageDecision(TriageVerdict.NeedsReview, TriageRules.CalverBump, "**Needs review:** calendar-versioned bump, `2024.10` -> `2025.1`. The version can't show whether it breaks anything."),
                decision);
        }

        [Fact]
        public void Calver_OmitsTheVersionsWhenNoneWereParsed()
        {
            var decision = Decide("renovate/foo", Jev("calver"), new BumpFacts(false, []));

            Assert.Equal("**Needs review:** calendar-versioned bump. The version can't show whether it breaks anything.", decision.Comment);
        }

        [Theory]
        [InlineData("other", TriageRules.OtherBump)]
        [InlineData("", TriageRules.UnknownBump)]
        [InlineData("Patch", TriageRules.UnknownBump)]
        [InlineData("prerelease", TriageRules.UnknownBump)]
        public void OtherAndUnknownBumps_NeedReviewWithTheCouldntTellComment(string bump, string reason)
        {
            var decision = Decide("renovate/pin-dependencies", Jev(bump));

            Assert.Equal(
                new TriageDecision(TriageVerdict.NeedsReview, reason, "**Needs review:** couldn't tell the bump type, or the PR bumps several packages in different ways."),
                decision);
        }

        [Fact]
        public void Digest_NeedsReviewWhenMoreThanADigestChanged()
        {
            var decision = Decide("renovate/foo", Jev("digest"), new BumpFacts(false, []));

            Assert.Equal(
                new TriageDecision(TriageVerdict.NeedsReview, TriageRules.DigestMismatch, "**Needs review:** marked as a digest bump, but more than a digest changed."),
                decision);
        }

        [Theory]
        [InlineData("patch", "minor")]
        [InlineData("minor", "patch")]
        [InlineData("patch", "major")]
        [InlineData("minor", "major")]
        public void PatchAndMinor_NeedReviewWhenAParsedVersionPairDisagreesWithJev(string jevType, string parsedKind)
        {
            var decision = Decide("renovate/ghcr.io-anthony-spruyt-foo", Jev(jevType), Versions(("1.0.0", "1.1.0", parsedKind)));

            Assert.Equal(
                new TriageDecision(TriageVerdict.NeedsReview, TriageRules.BumpTypeMismatch, $"**Needs review:** the versions in the diff (`1.0.0` -> `1.1.0`) don't match the bump type Jev picked (`{jevType}`)."),
                decision);
        }

        [Fact]
        public void PatchAndMinor_NeedReviewWhenAnyOfSeveralPairsDisagrees()
        {
            var decision = Decide("renovate/foo", Jev("patch"), Versions(("1.0.0", "1.0.1", "patch"), ("2.0.0", "3.0.0", "major")));

            Assert.Equal("**Needs review:** the versions in the diff (`2.0.0` -> `3.0.0`) don't match the bump type Jev picked (`patch`).", decision.Comment);
        }

        [Theory]
        [InlineData("patch")]
        [InlineData("minor")]
        public void PatchAndMinor_KeepJevsAnswerWhenTheParsedPairsAgree(string bump)
        {
            var decision = Decide("renovate/foo", Jev(bump), Versions(("1.0.0", "1.0.1", bump)));

            Assert.Equal(TriageRules.ChangelogPresent, decision.Reason);
        }

        [Theory]
        [InlineData("patch")]
        [InlineData("minor")]
        public void PatchAndMinor_NeedReviewWhenAChangedLineHadNoVersionToPair(string bump)
        {
            var decision = Decide("renovate/foo", Jev(bump), new BumpFacts(false, [], UnpairedVersions: true));

            Assert.Equal(
                new TriageDecision(TriageVerdict.NeedsReview, TriageRules.BumpTypeMismatch, $"**Needs review:** the versions in the diff don't match the bump type Jev picked (`{bump}`)."),
                decision);
        }

        [Fact]
        public void PatchAndMinor_NameTheDisagreeingPairBeforeFallingBackToTheUnpairedText()
        {
            var decision = Decide("renovate/foo", Jev("patch"), new BumpFacts(false, [new VersionChange("1.0.0", "1.1.0", "minor")], UnpairedVersions: true));

            Assert.Contains("`1.0.0` -> `1.1.0`", decision.Comment, StringComparison.Ordinal);
        }

        [Fact]
        public void PatchAndMinor_KeepJevsAnswerWhenNoPairWasFound()
        {
            Assert.Equal(TriageVerdict.Approve, Decide("renovate/foo", Jev("minor"), new BumpFacts(false, [])).Verdict);
        }

        [Fact]
        public void TheMismatchCheck_RunsBeforeTheAllowlist()
        {
            var decision = Decide("renovate/pin-dependencies", Jev("patch", changelog: 0), Versions(("1.0.0", "2.0.0", "major")));

            Assert.Equal(TriageRules.BumpTypeMismatch, decision.Reason);
        }

        [Theory]
        [InlineData("patch")]
        [InlineData("minor")]
        public void PatchAndMinor_AreApprovedForAnAllowlistedBranchWithoutAChangelog(string bump)
        {
            var decision = Decide("renovate/ghcr.io-anthony-spruyt-foo-1.x", Jev(bump, changelog: 0.1));

            Assert.Equal(new TriageDecision(TriageVerdict.Approve, TriageRules.Allowlisted, "**Auto-approved:** allowlisted."), decision);
        }

        [Theory]
        [InlineData("renovate/pin-dependencies")]
        [InlineData("renovate/anthony-spruyt-foo")]
        [InlineData("renovate/ghcr.io-anthony-spruyt-foo")]
        [InlineData("renovate/docker-build-push-action-digest")]
        [InlineData("renovate/docker-login-action-digest")]
        [InlineData("renovate/docker-metadata-action-digest")]
        [InlineData("renovate/docker-setup-buildx-action-digest")]
        [InlineData("renovate/aspruyt-foo")]
        [InlineData("renovate/node-24")]
        [InlineData("renovate/node-24.x")]
        [InlineData("renovate/aikidosec-safe-chain")]
        public void EveryAllowlistEntry_MatchesAsAPrefix(string branch)
        {
            Assert.Equal(TriageRules.Allowlisted, Decide(branch, Jev("patch", changelog: 0)).Reason);
        }

        [Theory]
        [InlineData("renovate/nodejs")]
        [InlineData("renovate/node-22")]
        [InlineData("renovate/other-pin-dependencies")]
        [InlineData("feature/pin-dependencies")]
        [InlineData("renovate/aikidosec")]
        [InlineData("Renovate/Node-24")]
        [InlineData("renovate/Node-24")]
        public void BranchesOutsideTheAllowlist_AreNotAllowlisted(string branch)
        {
            Assert.Equal(TriageRules.NoChangelog, Decide(branch, Jev("minor", changelog: 0)).Reason);
        }

        [Theory]
        [InlineData("patch")]
        [InlineData("minor")]
        public void PatchAndMinor_AreApprovedWithAChangelogAtTheThreshold(string bump)
        {
            var decision = Decide("renovate/foo", Jev(bump, changelog: 0.7));

            Assert.Equal(new TriageDecision(TriageVerdict.Approve, TriageRules.ChangelogPresent, $"**Auto-approved:** {bump} bump with changelog."), decision);
        }

        [Theory]
        [InlineData("patch")]
        [InlineData("minor")]
        public void PatchAndMinor_WithoutAChangelog_NeedReviewWithTheNoChangelogComment(string bump)
        {
            var decision = Decide("renovate/foo", Jev(bump, changelog: 0.699));

            Assert.Equal(new TriageDecision(TriageVerdict.NeedsReview, TriageRules.NoChangelog, "**Needs review:** no changelog found for this bump."), decision);
        }

        [Fact]
        public void TheOtherFixedTexts_AreAsApproved()
        {
            Assert.Equal("**Needs review:** CI didn't finish within 2 hours.", TriageRules.CiTimeoutDecision().Comment);
            Assert.Equal("**Needs review:** the triage check couldn't run (Jev error).", TriageRules.JevErrorDecision().Comment);
        }
    }

    public class Comments
    {
        private const string Repo = "anthony-spruyt/widgets";

        [Fact]
        public void CiFailed_NamesTheShortShaAndLinksEachFailedCheck()
        {
            var decision = TriageRules.CiFailedDecision(Repo, Head, [new GitHubCheckRun(11, "build", "completed", "failure", "github-actions"), new GitHubCheckRun(12, "test (x)", "completed", "cancelled", "github-actions")]);

            Assert.Equal(TriageVerdict.NeedsReview, decision.Verdict);
            Assert.Equal(TriageRules.CiFailed, decision.Reason);
            Assert.Equal(
                "**Needs review:** CI failed on `0123456`. Failed: [`build`](https://github.com/anthony-spruyt/widgets/runs/11), [`test (x)`](https://github.com/anthony-spruyt/widgets/runs/12).",
                decision.Comment);
        }

        [Fact]
        public void CiFailed_WithoutNamedChecks_StatesOnlyTheSha()
        {
            Assert.Equal("**Needs review:** CI failed on `0123456`.", TriageRules.CiFailedDecision(Repo, Head, []).Comment);
        }

        [Fact]
        public void CiFailed_ListsAtMostTenChecks()
        {
            var failed = Enumerable.Range(1, 12).Select(i => new GitHubCheckRun(i, $"job-{i}", "completed", "failure", "github-actions")).ToList();

            var comment = TriageRules.CiFailedDecision(Repo, Head, failed).Comment!;

            Assert.Contains("[`job-10`](", comment, StringComparison.Ordinal);
            Assert.DoesNotContain("[`job-11`](", comment, StringComparison.Ordinal);
            Assert.EndsWith(" and 2 more.", comment, StringComparison.Ordinal);
        }

        [Fact]
        public void CiFailed_DefusesMarkdownInACheckName()
        {
            var comment = TriageRules.CiFailedDecision(Repo, Head, [new GitHubCheckRun(1, "a](https://evil.test) `x`", "completed", "failure", "github-actions")]).Comment!;

            Assert.Equal("**Needs review:** CI failed on `0123456`. Failed: [`a)(https://evil.test) 'x'`](https://github.com/anthony-spruyt/widgets/runs/1).", comment);
        }

        [Theory]
        [InlineData("@octocat please look", "`@octocat please look`")]
        [InlineData("<img src=x onerror=y>", "`<img src=x onerror=y>`")]
        [InlineData("a <!-- b", "`a <!-- b`")]
        [InlineData("line one\nline two", "`line one line two`")]
        [InlineData("  ", "`check`")]
        public void CiFailed_ShowsACheckNameAsInlineCodeSoItCannotMentionOrInjectHtml(string name, string shown)
        {
            var comment = TriageRules.CiFailedDecision(Repo, Head, [new GitHubCheckRun(1, name, "completed", "failure", "github-actions")]).Comment!;

            Assert.Equal($"**Needs review:** CI failed on `0123456`. Failed: [{shown}](https://github.com/anthony-spruyt/widgets/runs/1).", comment);
        }

        [Fact]
        public void FailedChecks_AreTheCompletedConsideredNonPassingOnes()
        {
            var failed = TriageRules.FailedChecks(
            [
                Run("build"),
                Run("test", conclusion: "failure"),
                Run("skynet/x", conclusion: "failure"),
                Run("Mergify Merge Protections", conclusion: "failure"),
                Run("lint", status: "in_progress", conclusion: null),
                Run("bot", conclusion: "failure", app: "mergify"),
                Run("cancelled", conclusion: "cancelled"),
            ]);

            Assert.Equal(["test", "cancelled"], failed.Select(c => c.Name));
        }

        [Fact]
        public void HighRisk_NamesTheMatchedTerm()
        {
            var decision = TriageRules.HighRiskDecision("traefik");

            Assert.Equal(new TriageDecision(TriageVerdict.NeedsReview, TriageRules.HighRisk, "**Needs review:** `traefik` is on the high-risk list. These always get a human look."), decision);
        }

        [Theory]
        [InlineData("renovate/traefik-3.x", "traefik")]
        [InlineData("renovate/rook-ceph", "rook")]
        [InlineData("renovate/czkawka", null)]
        [InlineData("renovate/n8n-mcp", null)]
        public void RiskyTerm_IsTheFirstMatchOrNothing(string branch, string? term)
        {
            Assert.Equal(term, TriageRules.RiskyTerm(branch));
        }

        [Theory]
        [InlineData("**Auto-approved:** digest bump.", "**Auto-approved:** new commit `0123456`, digest bump.")]
        [InlineData("**Auto-approved:** allowlisted.", "**Auto-approved:** new commit `0123456`, allowlisted.")]
        [InlineData("**Auto-approved:**", "**Auto-approved:** new commit `0123456`.")]
        [InlineData(null, "**Auto-approved:** new commit `0123456`.")]
        public void StickyApprovalText_NamesTheCommitAndTheLowercasedReason(string? approvalComment, string expected)
        {
            Assert.Equal(expected, TriageRules.StickyApprovalText(Head, approvalComment));
        }

        [Theory]
        [InlineData("a1b2c3d4e5f6", "a1b2c3d")]
        [InlineData("abc", "abc")]
        public void ShortSha_IsSevenCharacters(string sha, string expected)
        {
            Assert.Equal(expected, TriageRules.ShortSha(sha));
        }
    }

    public class Owner
    {
        [Theory]
        [InlineData("anthony-spruyt/widgets", true)]
        [InlineData("Anthony-Spruyt/widgets", true)]
        [InlineData("octo/widgets", false)]
        [InlineData("anthony-spruyt-evil/widgets", false)]
        [InlineData("not-a-repo", false)]
        [InlineData("", false)]
        public void OnlyTheOwnersReposAreTriaged(string repo, bool allowed)
        {
            Assert.Equal(allowed, TriageRules.IsAllowedOwner(repo));
        }
    }

    public class PrecheckAndClimbing
    {
        [Theory]
        [InlineData(PrecheckVerdict.DiffNotVersionOnly, TriageRules.PrecheckDiff)]
        [InlineData(PrecheckVerdict.BodyFlagged, TriageRules.PrecheckFlagged)]
        [InlineData(PrecheckVerdict.BodyUnchecked, TriageRules.PrecheckUnchecked)]
        public void AFailedPrecheck_IsANeverClimbingNeedsReviewWithANeutralComment(PrecheckVerdict verdict, string reason)
        {
            var decision = TriageRules.PrecheckDecision(verdict);

            Assert.Equal(TriageVerdict.NeedsReview, decision.Verdict);
            Assert.Equal(reason, decision.Reason);
            Assert.StartsWith("**Needs review:**", decision.Comment, StringComparison.Ordinal);
            Assert.False(TriageRules.Climbs(decision));
        }

        [Fact]
        public void APassedPrecheck_HasNoDecision()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => TriageRules.PrecheckDecision(PrecheckVerdict.Passed));
        }

        [Theory]
        [InlineData(TriageRules.CiFailed)]
        [InlineData(TriageRules.CiTimeout)]
        [InlineData(TriageRules.Breaking)]
        [InlineData(TriageRules.MajorBump)]
        [InlineData(TriageRules.CalverBump)]
        [InlineData(TriageRules.HighRisk)]
        [InlineData(TriageRules.JevError)]
        [InlineData(TriageRules.NoChangelog)]
        [InlineData(TriageRules.OtherBump)]
        public void EveryOtherNeedsReviewReason_Climbs(string reason)
        {
            Assert.True(TriageRules.Climbs(new TriageDecision(TriageVerdict.NeedsReview, reason, "c")));
        }

        [Theory]
        [InlineData(TriageRules.NonRenovateCommit)]
        [InlineData(MigrationDetector.Reason)]
        public void TheNeverClimbReasons_DoNotClimb(string reason)
        {
            Assert.False(TriageRules.Climbs(new TriageDecision(TriageVerdict.NeedsReview, reason, "c")));
        }

        [Fact]
        public void AnApproval_DoesNotClimb()
        {
            Assert.False(TriageRules.Climbs(new TriageDecision(TriageVerdict.Approve, TriageRules.Allowlisted, "c")));
        }
    }

    public class SafeChecks
    {
        [Fact]
        public void APassedHead_ApprovesOnAnOrdinaryBranch()
        {
            Assert.Equal(SafeAction.Approve, TriageRules.AssessSafe(CiState.Passed, "renovate/czkawka"));
        }

        [Fact]
        public void ARedHead_IsFixable_NotSafe()
        {
            Assert.Equal(SafeAction.Fixable, TriageRules.AssessSafe(CiState.Failed, "renovate/czkawka"));
        }

        [Fact]
        public void APendingHead_NeverGetsSafe_AndWaitsForCi()
        {
            Assert.Equal(SafeAction.WaitForCi, TriageRules.AssessSafe(CiState.Pending, "renovate/czkawka"));
        }

        [Fact]
        public void AHighRiskBranch_IsCommentOnly()
        {
            Assert.Equal(SafeAction.CommentOnly, TriageRules.AssessSafe(CiState.Passed, "renovate/cilium-1.x"));
        }

        [Fact]
        public void ARedHighRiskBranch_IsStillFixable()
        {
            Assert.Equal(SafeAction.Fixable, TriageRules.AssessSafe(CiState.Failed, "renovate/cilium-1.x"));
        }

        [Fact]
        public void APendingHighRiskBranch_WaitsForCi()
        {
            Assert.Equal(SafeAction.WaitForCi, TriageRules.AssessSafe(CiState.Pending, "renovate/cilium-1.x"));
        }
    }
}
