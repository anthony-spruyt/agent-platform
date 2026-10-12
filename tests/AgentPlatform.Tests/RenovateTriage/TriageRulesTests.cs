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

    private static TriageDecision Decide(string branch, JevAnswers jev) => TriageRules.DecideFromJev(branch, jev);

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
        [InlineData("agent/needs-review")]
        [InlineData("agent/revert")]
        [InlineData("blocked")]
        public void ABlockingLabel_IsSkipped(string label)
        {
            Assert.Equal(TriageRules.BlockingLabel, TriageRules.SkipReason(29139614, ["dep/minor", label]));
        }

        [Theory]
        [InlineData("agent/needs-review")]
        [InlineData("agent/auto-approved")]
        public void InShadowMode_TheLabelsOfTheLivePath_AreIgnored(string label)
        {
            Assert.Null(TriageRules.SkipReason(29139614, [label], shadow: true));
        }

        [Theory]
        [InlineData("agent/revert")]
        [InlineData("blocked")]
        public void InShadowMode_OtherBlockingLabels_StillSkip(string label)
        {
            Assert.Equal(TriageRules.BlockingLabel, TriageRules.SkipReason(29139614, ["agent/needs-review", label], shadow: true));
        }

        [Theory]
        [InlineData("agent/needs-review")]
        [InlineData("agent/revert")]
        [InlineData("blocked")]
        public void OutsideShadowMode_EveryBlockingLabel_Skips(string label)
        {
            Assert.Equal(TriageRules.BlockingLabel, TriageRules.SkipReason(29139614, [label], shadow: false));
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
            Assert.False(TriageRules.IsApproved([Review(TriageRules.TriageBotLogin, "APPROVED", "CONTRIBUTOR")], Head, shadow: true));
        }

        [Fact]
        public void InShadowMode_AHumanApproval_StillCounts()
        {
            Assert.True(TriageRules.IsApproved([Review(TriageRules.TriageBotLogin, "APPROVED", "CONTRIBUTOR"), Review("alice", "APPROVED")], Head, shadow: true));
        }

        [Fact]
        public void InShadowMode_TheTriageAppBotRequestingChanges_IsIgnored()
        {
            Assert.True(TriageRules.IsApproved([Review("alice", "APPROVED"), Review(TriageRules.TriageBotLogin, "CHANGES_REQUESTED", "CONTRIBUTOR")], Head, shadow: true));
        }

        [Fact]
        public void OutsideShadowMode_TheTriageAppBot_Counts()
        {
            Assert.True(TriageRules.IsApproved([Review(TriageRules.TriageBotLogin, "APPROVED", "CONTRIBUTOR")], Head, shadow: false));
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
                new TriageDecision(TriageVerdict.NeedsReview, "non-renovate-commit", "This dependency bump has commits not made by Renovate and requires review."),
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

            Assert.Equal(new TriageDecision(TriageVerdict.NeedsReview, TriageRules.Breaking, TriageRules.BreakingComment), decision);
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

            Assert.Equal(new TriageDecision(TriageVerdict.Approve, TriageRules.Digest, null), decision);
        }

        [Fact]
        public void Major_NeedsReviewWithTheBreakingComment()
        {
            var decision = Decide("renovate/pin-dependencies", Jev("major"));

            Assert.Equal(new TriageDecision(TriageVerdict.NeedsReview, TriageRules.MajorBump, TriageRules.BreakingComment), decision);
        }

        [Theory]
        [InlineData("calver", TriageRules.CalverBump)]
        [InlineData("other", TriageRules.OtherBump)]
        [InlineData("", TriageRules.UnknownBump)]
        [InlineData("Patch", TriageRules.UnknownBump)]
        [InlineData("prerelease", TriageRules.UnknownBump)]
        public void CalverOtherAndUnknownBumps_NeedReviewWithoutAComment(string bump, string reason)
        {
            var decision = Decide("renovate/pin-dependencies", Jev(bump));

            Assert.Equal(new TriageDecision(TriageVerdict.NeedsReview, reason, null), decision);
        }

        [Theory]
        [InlineData("patch")]
        [InlineData("minor")]
        public void PatchAndMinor_AreApprovedForAnAllowlistedBranchWithoutAChangelog(string bump)
        {
            var decision = Decide("renovate/ghcr.io-anthony-spruyt-foo-1.x", Jev(bump, changelog: 0.1));

            Assert.Equal(new TriageDecision(TriageVerdict.Approve, TriageRules.Allowlisted, null), decision);
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

            Assert.Equal(new TriageDecision(TriageVerdict.Approve, TriageRules.ChangelogPresent, null), decision);
        }

        [Theory]
        [InlineData("patch")]
        [InlineData("minor")]
        public void PatchAndMinor_WithoutAChangelog_NeedReviewWithTheNoChangelogComment(string bump)
        {
            var decision = Decide("renovate/foo", Jev(bump, changelog: 0.699));

            Assert.Equal(new TriageDecision(TriageVerdict.NeedsReview, TriageRules.NoChangelog, TriageRules.NoChangelogComment), decision);
        }

        [Fact]
        public void TheCommentTexts_AreExactlyTheOnesTheOldWorkflowPosted()
        {
            Assert.Equal("Flagged for review as CI failed.", TriageRules.CiFailedComment);
            Assert.Equal("This is a high risk dependency bump and requires review.", TriageRules.HighRiskComment);
            Assert.Equal("This dependency bump has breaking changes and requires review.", TriageRules.BreakingComment);
            Assert.Equal("This dependency bump has no changelog and requires review.", TriageRules.NoChangelogComment);
        }
    }
}
