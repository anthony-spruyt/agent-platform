using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.RenovateTriage;

namespace AgentPlatform.Tests.RenovateTriage;

public class TriageRulesTests
{
    private static GitHubCheckRun Run(string name, string status = "completed", string? conclusion = "success") =>
        new(1, name, status, conclusion, "github-actions");

    private static GitHubReview Review(string? user, string state) =>
        new(1, user, state, null, null);

    private static JevAnswers Jev(string bump, double breaking = 0.04, double changelog = 0.78) =>
        new(breaking, bump, changelog);

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
            Assert.False(TriageRules.IsApproved([]));
        }

        [Fact]
        public void AnApproval_IsApproved()
        {
            Assert.True(TriageRules.IsApproved([Review("alice", "APPROVED")]));
        }

        [Fact]
        public void CommentsDoNotChangeAnApproval()
        {
            Assert.True(TriageRules.IsApproved([Review("alice", "APPROVED"), Review("alice", "COMMENTED")]));
        }

        [Fact]
        public void OnlyComments_IsNotApproved()
        {
            Assert.False(TriageRules.IsApproved([Review("alice", "COMMENTED")]));
        }

        [Fact]
        public void TheLatestNonCommentReviewOfAUserWins()
        {
            Assert.False(TriageRules.IsApproved([Review("alice", "APPROVED"), Review("alice", "CHANGES_REQUESTED")]));
            Assert.True(TriageRules.IsApproved([Review("alice", "CHANGES_REQUESTED"), Review("alice", "APPROVED")]));
        }

        [Fact]
        public void ADismissedReview_ReplacesTheEarlierApproval()
        {
            Assert.False(TriageRules.IsApproved([Review("alice", "APPROVED"), Review("alice", "DISMISSED")]));
        }

        [Fact]
        public void ChangesRequestedByAnyoneBeatsApprovals()
        {
            Assert.False(TriageRules.IsApproved([Review("alice", "APPROVED"), Review("bob", "CHANGES_REQUESTED"), Review("carol", "APPROVED")]));
        }

        [Fact]
        public void ReviewsWithoutAnAuthor_AreGroupedTogether()
        {
            Assert.False(TriageRules.IsApproved([Review(null, "APPROVED"), Review(null, "CHANGES_REQUESTED")]));
        }
    }

    public class ChecksAssessment
    {
        [Fact]
        public void NoChecks_Pass()
        {
            Assert.Equal(CiState.Passed, TriageRules.AssessChecks([]));
        }

        [Theory]
        [InlineData("success")]
        [InlineData("neutral")]
        [InlineData("skipped")]
        public void CompletedChecksWithAPassingConclusion_Pass(string conclusion)
        {
            Assert.Equal(CiState.Passed, TriageRules.AssessChecks([Run("build", conclusion: conclusion), Run("test")]));
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
            Assert.Equal(CiState.Failed, TriageRules.AssessChecks([Run("test"), Run("build", conclusion: conclusion)]));
        }

        [Theory]
        [InlineData("queued")]
        [InlineData("in_progress")]
        [InlineData("waiting")]
        [InlineData("pending")]
        [InlineData("requested")]
        public void ACheckThatHasNotCompleted_IsPending(string status)
        {
            Assert.Equal(CiState.Pending, TriageRules.AssessChecks([Run("test"), Run("build", status, null)]));
        }

        [Fact]
        public void PendingWinsOverFailed()
        {
            Assert.Equal(CiState.Pending, TriageRules.AssessChecks([Run("test", conclusion: "failure"), Run("build", "in_progress", null)]));
        }

        [Theory]
        [InlineData("renovate/stability-days")]
        [InlineData("Mergify Merge Protections")]
        [InlineData("Mergify")]
        public void ChecksIgnoredEverywhere_NeverBlockOrFail(string name)
        {
            Assert.Equal(CiState.Passed, TriageRules.AssessChecks([Run("test"), Run(name, "in_progress", null)]));
            Assert.Equal(CiState.Passed, TriageRules.AssessChecks([Run("test"), Run(name, conclusion: "failure")]));
        }

        [Fact]
        public void SkynetChecks_DoNotFailButStillHoldTheJudgementWhilePending()
        {
            Assert.Equal(CiState.Passed, TriageRules.AssessChecks([Run("test"), Run("skynet/triage", conclusion: "failure")]));
            Assert.Equal(CiState.Pending, TriageRules.AssessChecks([Run("test"), Run("skynet/triage", "in_progress", null)]));
        }

        [Fact]
        public void NamesThatMerelyContainAnIgnoredName_AreStillJudged()
        {
            Assert.Equal(CiState.Failed, TriageRules.AssessChecks([Run("not-Mergify", conclusion: "failure")]));
            Assert.Equal(CiState.Failed, TriageRules.AssessChecks([Run("ci/skynet/x", conclusion: "failure")]));
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
            var decision = TriageRules.DecideFromJev("renovate/foo", Jev("patch", breaking: 0.66));

            Assert.Equal(new TriageDecision(TriageVerdict.NeedsReview, TriageRules.Breaking, TriageRules.BreakingComment), decision);
        }

        [Fact]
        public void JustUnderTheBreakingThreshold_IsNotBreaking()
        {
            var decision = TriageRules.DecideFromJev("renovate/foo", Jev("digest", breaking: 0.659));

            Assert.Equal(TriageVerdict.Approve, decision.Verdict);
        }

        [Fact]
        public void BreakingBeatsADigestBump()
        {
            Assert.Equal(TriageVerdict.NeedsReview, TriageRules.DecideFromJev("renovate/foo", Jev("digest", breaking: 0.9)).Verdict);
        }

        [Fact]
        public void Digest_IsApprovedEvenWithoutAChangelogOrAnAllowlistedBranch()
        {
            var decision = TriageRules.DecideFromJev("renovate/foo", Jev("digest", changelog: 0));

            Assert.Equal(new TriageDecision(TriageVerdict.Approve, TriageRules.Digest, null), decision);
        }

        [Fact]
        public void Major_NeedsReviewWithTheBreakingComment()
        {
            var decision = TriageRules.DecideFromJev("renovate/pin-dependencies", Jev("major"));

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
            var decision = TriageRules.DecideFromJev("renovate/pin-dependencies", Jev(bump));

            Assert.Equal(new TriageDecision(TriageVerdict.NeedsReview, reason, null), decision);
        }

        [Theory]
        [InlineData("patch")]
        [InlineData("minor")]
        public void PatchAndMinor_AreApprovedForAnAllowlistedBranchWithoutAChangelog(string bump)
        {
            var decision = TriageRules.DecideFromJev("renovate/ghcr.io-anthony-spruyt-foo-1.x", Jev(bump, changelog: 0.1));

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
        [InlineData("Renovate/Node-24")]
        public void EveryAllowlistEntry_MatchesAsAPrefix(string branch)
        {
            Assert.Equal(TriageRules.Allowlisted, TriageRules.DecideFromJev(branch, Jev("patch", changelog: 0)).Reason);
        }

        [Theory]
        [InlineData("renovate/nodejs")]
        [InlineData("renovate/node-22")]
        [InlineData("renovate/other-pin-dependencies")]
        [InlineData("feature/pin-dependencies")]
        [InlineData("renovate/aikidosec")]
        public void BranchesOutsideTheAllowlist_AreNotAllowlisted(string branch)
        {
            Assert.Equal(TriageRules.NoChangelog, TriageRules.DecideFromJev(branch, Jev("minor", changelog: 0)).Reason);
        }

        [Theory]
        [InlineData("patch")]
        [InlineData("minor")]
        public void PatchAndMinor_AreApprovedWithAChangelogAtTheThreshold(string bump)
        {
            var decision = TriageRules.DecideFromJev("renovate/foo", Jev(bump, changelog: 0.7));

            Assert.Equal(new TriageDecision(TriageVerdict.Approve, TriageRules.ChangelogPresent, null), decision);
        }

        [Theory]
        [InlineData("patch")]
        [InlineData("minor")]
        public void PatchAndMinor_WithoutAChangelog_NeedReviewWithTheNoChangelogComment(string bump)
        {
            var decision = TriageRules.DecideFromJev("renovate/foo", Jev(bump, changelog: 0.699));

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
