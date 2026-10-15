using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AgentPlatform.Host.GitHub;

namespace AgentPlatform.Host.RenovateTriage;

[JsonConverter(typeof(JsonStringEnumConverter<TriageVerdict>))]
public enum TriageVerdict
{
    Approve,
    NeedsReview,
    Safe,
    Fixable,
    Blocked,
    Owner,
}

[JsonConverter(typeof(JsonStringEnumConverter<CiState>))]
public enum CiState
{
    Passed,
    Failed,
    Pending,
}

public sealed record JevAnswers(double Breaking, string BumpType, double Changelog);

public sealed record TriageDecision(TriageVerdict Verdict, string Reason, string? Comment);

public static partial class TriageRules
{
    public const long RenovateUserId = 29139614;

    public const string TriageBotLogin = "skynet-rw[bot]";

    public const int MaxListedCommits = 250;

    public const double BreakingThreshold = 0.66;
    public const double ChangelogThreshold = 0.7;

    public const string AllowedOwner = "anthony-spruyt";

    public const string StickyMarker = "<!-- agent-platform:renovate-triage -->";
    public const string NeedsReviewLabel = "agent/needs-human-review";
    public const string NeedsTriageLabel = "agent/needs-triage";
    public const string SafeLabel = "agent/safe";
    public const string FixableLabel = "agent/fixable";
    public const string BlockedLabel = "blocked";
    public const string RevertLabel = "agent/revert";
    public const string AutoApprovedLabel = "agent/auto-approved";

    public const string NotOpen = "not-open";
    public const string ForeignRepo = "foreign-repo";
    public const string ForeignOwner = "foreign-owner";
    public const string NotRenovate = "not-renovate";
    public const string BlockingLabel = "blocking-label";
    public const string AlreadyApproved = "already-approved";
    public const string NonRenovateCommit = "non-renovate-commit";
    public const string CiFailed = "ci-failed";
    public const string CiTimeout = "ci-timeout";
    public const string HighRisk = "high-risk";
    public const string JevError = "jev-error";
    public const string Breaking = "breaking";
    public const string Digest = "digest";
    public const string MajorBump = "major-bump";
    public const string CalverBump = "calver-bump";
    public const string OtherBump = "other-bump";
    public const string UnknownBump = "unknown-bump";
    public const string Allowlisted = "allowlisted";
    public const string ChangelogPresent = "changelog-present";
    public const string NoChangelog = "no-changelog";
    public const string BumpTypeMismatch = "bump-type-mismatch";
    public const string DigestMismatch = "digest-mismatch";
    public const string PrecheckDiff = "precheck-diff";
    public const string PrecheckFlagged = "precheck-flagged";
    public const string PrecheckUnchecked = "precheck-unchecked";

    public const string NonRenovateCommitComment = "**Needs review:** this PR has commits that Renovate didn't make.";
    public const string CiTimeoutComment = "**Needs review:** CI didn't finish within 2 hours.";
    public const string OtherBumpComment = "**Needs review:** couldn't tell the bump type, or the PR bumps several packages in different ways.";
    public const string NoChangelogComment = "**Needs review:** no changelog found for this bump.";
    public const string JevErrorComment = "**Needs review:** the triage check couldn't run (Jev error).";
    public const string DigestMismatchComment = "**Needs review:** marked as a digest bump, but more than a digest changed.";
    public const string PrecheckDiffComment = "**Needs review:** this PR changes more than version and digest updates.";
    public const string PrecheckFlaggedComment = "**Needs review:** some of this PR's content is held for a human look.";
    public const string PrecheckUncheckedComment = "**Needs review:** some of this PR's content could not be checked.";
    public const string SafeApprovalComment = "**Auto-approved:** the agent judged this PR safe.";
    public const string DigestApprovalComment = "**Auto-approved:** digest bump.";
    public const string AllowlistedApprovalComment = "**Auto-approved:** allowlisted.";

    private const int MaxListedFailedChecks = 10;

    public const string NeedsReviewPrefix = "**Needs review:**";
    private const string FixablePrefix = "**Fixable:**";
    private const string AutoApprovedPrefix = "**Auto-approved:**";

    private const string RenovateLogin = "renovate[bot]";
    private const string RenovateAuthorEmail = "29139614+renovate[bot]@users.noreply.github.com";
    private const string GitHubCommitterEmail = "noreply@github.com";
    private const string GitHubActionsApp = "github-actions";
    private const string MergifyApp = "mergify";

    private static readonly string[] _trustedAssociations = ["OWNER", "MEMBER", "COLLABORATOR"];

    private static readonly string[] _blockingLabels = [NeedsReviewLabel, NeedsTriageLabel, RevertLabel, BlockedLabel];

    private static readonly string[] _neverClimb = [NonRenovateCommit, PrecheckDiff, PrecheckFlagged, PrecheckUnchecked, MigrationDetector.Reason];

    private static readonly string[] _livePathLabels = [NeedsReviewLabel, NeedsTriageLabel, AutoApprovedLabel];

    private static readonly string[] _ignoredCheckPrefixes = ["Mergify", "renovate/stability-days"];

    private static readonly string[] _notFailingCheckPrefixes = ["skynet/", .. _ignoredCheckPrefixes];

    private static readonly string[] _allowlist =
    [
        "renovate/pin-dependencies",
        "renovate/anthony-spruyt",
        "renovate/ghcr.io-anthony-spruyt",
        "renovate/docker-build-push-action-digest",
        "renovate/docker-login-action-digest",
        "renovate/docker-metadata-action-digest",
        "renovate/docker-setup-buildx-action-digest",
        "renovate/aspruyt",
        "renovate/node-24",
        "renovate/aikidosec-safe-chain",
    ];

    public static string? SkipReason(long? authorId, IReadOnlyCollection<string> labels, bool ignoreTriageSignals = false, bool honourNeedsReviewLabel = false, bool hasPendingRow = false)
    {
        ArgumentNullException.ThrowIfNull(labels);
        if (authorId != RenovateUserId)
        {
            return NotRenovate;
        }

        return labels.Any(l => IsBlockingLabel(l, ignoreTriageSignals, honourNeedsReviewLabel, hasPendingRow)) ? BlockingLabel : null;
    }

    public static bool IsBlocking(string label, bool hasPendingRow)
    {
        ArgumentNullException.ThrowIfNull(label);
        return _blockingLabels.Contains(label, StringComparer.Ordinal)
            && !(hasPendingRow && string.Equals(label, NeedsTriageLabel, StringComparison.Ordinal));
    }

    public static bool IsApproving(TriageVerdict verdict) => verdict is TriageVerdict.Approve or TriageVerdict.Safe;

    public static SafeAction AssessSafe(CiState ci, string branch)
    {
        ArgumentNullException.ThrowIfNull(branch);
        return ci switch
        {
            CiState.Failed => SafeAction.Fixable,
            CiState.Pending => SafeAction.WaitForCi,
            _ => IsRiskyBranch(branch) ? SafeAction.CommentOnly : SafeAction.Approve,
        };
    }

    public static bool IsNotOpen(string state, bool merged, bool shadow)
    {
        ArgumentNullException.ThrowIfNull(state);
        var open = state == "open";
        return shadow ? !open && !merged : !open || merged;
    }

    public static bool IsSameRepo(string repo, string? headRepo, string? baseRepo)
    {
        ArgumentNullException.ThrowIfNull(repo);
        return string.Equals(headRepo, repo, StringComparison.OrdinalIgnoreCase) && string.Equals(baseRepo, repo, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsApproved(IReadOnlyList<GitHubReview> reviews, string headSha, bool ignoreTriageSignals = false)
    {
        ArgumentNullException.ThrowIfNull(reviews);
        ArgumentNullException.ThrowIfNull(headSha);
        var latest = new Dictionary<string, GitHubReview>(StringComparer.Ordinal);
        foreach (var review in reviews.Where(r => !(ignoreTriageSignals && IsTriageBot(r)) && IsTrustedReviewer(r) && !string.Equals(r.State, "COMMENTED", StringComparison.OrdinalIgnoreCase)))
        {
            latest[review.AuthorLogin ?? string.Empty] = review;
        }

        if (latest.Values.Any(r => string.Equals(r.State, "CHANGES_REQUESTED", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return headSha.Length > 0
            && latest.Values.Any(r => string.Equals(r.State, "APPROVED", StringComparison.OrdinalIgnoreCase) && string.Equals(r.CommitId, headSha, StringComparison.Ordinal));
    }

    public static bool IsAllowedOwner(string repo)
    {
        ArgumentNullException.ThrowIfNull(repo);
        return GitHubRepoName.TryParse(repo, out var owner, out _) && string.Equals(owner, AllowedOwner, StringComparison.OrdinalIgnoreCase);
    }

    public static CiState AssessChecks(IReadOnlyList<GitHubCheckRun> checks, IReadOnlyList<GitHubCheckSuite> suites)
    {
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(suites);
        var considered = ConsideredChecks(checks);
        var consideredSuites = suites.Where(IsConsideredSuite);
        if (considered.Count == 0
            || considered.Any(c => !IsCompleted(c.Status))
            || consideredSuites.Any(s => !IsCompleted(s.Status)))
        {
            return CiState.Pending;
        }

        return considered.Any(IsFailing) ? CiState.Failed : CiState.Passed;
    }

    public static IReadOnlyList<GitHubCheckRun> FailedChecks(IReadOnlyList<GitHubCheckRun> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        return [.. ConsideredChecks(checks).Where(c => IsCompleted(c.Status) && IsFailing(c))];
    }

    public static bool AreAllRenovateCommits(IReadOnlyList<GitHubCommit> commits, int prCommitCount)
    {
        ArgumentNullException.ThrowIfNull(commits);
        return prCommitCount is > 0 and < MaxListedCommits && commits.Count == prCommitCount && commits.All(IsRenovateCommit);
    }

    public static int LeadingRenovateCommits(IReadOnlyList<GitHubCommit> commits)
    {
        ArgumentNullException.ThrowIfNull(commits);
        var count = 0;
        while (count < commits.Count && IsRenovateCommit(commits[count]))
        {
            count++;
        }

        return count;
    }

    public static GitHubCommit? StatedCommit(IReadOnlyList<GitHubCommit> commits)
    {
        var count = LeadingRenovateCommits(commits);
        return count == 0 ? null : commits[count - 1];
    }

    public static bool IsRiskyBranch(string branch) => RiskyTerm(branch) is not null;

    public static string? RiskyTerm(string branch)
    {
        ArgumentNullException.ThrowIfNull(branch);
        var lower = branch.ToLowerInvariant();
        var match = RiskyBranch().Match(lower);
        return match.Success && !lower.Contains("n8n-mcp", StringComparison.Ordinal) ? match.Value : null;
    }

    public static TriageDecision DecideFromJev(string branch, JevAnswers jev, BumpFacts facts)
    {
        ArgumentNullException.ThrowIfNull(branch);
        ArgumentNullException.ThrowIfNull(jev);
        ArgumentNullException.ThrowIfNull(facts);
        if (jev.Breaking >= BreakingThreshold)
        {
            return new TriageDecision(
                TriageVerdict.NeedsReview,
                Breaking,
                $"{NeedsReviewPrefix} likely breaking changes (Jev score {jev.Breaking.ToString("0.00", CultureInfo.InvariantCulture)}).");
        }

        switch (jev.BumpType)
        {
            case "digest":
                return facts.DigestOnly
                    ? new TriageDecision(TriageVerdict.Approve, Digest, DigestApprovalComment)
                    : new TriageDecision(TriageVerdict.NeedsReview, DigestMismatch, DigestMismatchComment);
            case "major":
                return new TriageDecision(
                    TriageVerdict.NeedsReview,
                    MajorBump,
                    $"{NeedsReviewPrefix} major bump{VersionSuffix(facts.Versions.FirstOrDefault(v => v.Kind == BumpAnalyzer.Major && !IsCalendar(v)))}.");
            case "calver":
                return new TriageDecision(
                    TriageVerdict.NeedsReview,
                    CalverBump,
                    $"{NeedsReviewPrefix} calendar-versioned bump{VersionSuffix(facts.Versions.FirstOrDefault(IsCalendar))}. The version can't show whether it breaks anything.");
            case "other":
                return new TriageDecision(TriageVerdict.NeedsReview, OtherBump, OtherBumpComment);
            case "patch":
            case "minor":
                return DecidePatchOrMinor(branch, jev, facts);
            default:
                return new TriageDecision(TriageVerdict.NeedsReview, UnknownBump, OtherBumpComment);
        }
    }

    public static TriageDecision NonRenovateCommitDecision() => new(TriageVerdict.NeedsReview, NonRenovateCommit, NonRenovateCommitComment);

    public static TriageDecision CiFailedDecision(string repo, string headSha, IReadOnlyList<GitHubCheckRun> failed)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(headSha);
        ArgumentNullException.ThrowIfNull(failed);
        var comment = new StringBuilder($"{NeedsReviewPrefix} CI failed on `{ShortSha(headSha)}`.");
        if (failed.Count > 0)
        {
            var links = failed.Take(MaxListedFailedChecks).Select(c => $"[{LinkText(c.Name)}](https://github.com/{repo}/runs/{c.Id.ToString(CultureInfo.InvariantCulture)})");
            comment.Append(" Failed: ").Append(string.Join(", ", links));
            comment.Append(failed.Count > MaxListedFailedChecks ? $" and {failed.Count - MaxListedFailedChecks} more." : ".");
        }

        return new TriageDecision(TriageVerdict.NeedsReview, CiFailed, comment.ToString());
    }

    public static TriageDecision SafeOnRedHeadDecision(string repo, string headSha, IReadOnlyList<GitHubCheckRun> failed)
    {
        var ci = CiFailedDecision(repo, headSha, failed);
        return ci with { Verdict = TriageVerdict.Fixable, Comment = $"{FixablePrefix}{ci.Comment![NeedsReviewPrefix.Length..]}" };
    }

    public static TriageDecision PrecheckDecision(PrecheckVerdict verdict) => verdict switch
    {
        PrecheckVerdict.DiffNotVersionOnly => new TriageDecision(TriageVerdict.NeedsReview, PrecheckDiff, PrecheckDiffComment),
        PrecheckVerdict.BodyFlagged => new TriageDecision(TriageVerdict.NeedsReview, PrecheckFlagged, PrecheckFlaggedComment),
        PrecheckVerdict.BodyUnchecked => new TriageDecision(TriageVerdict.NeedsReview, PrecheckUnchecked, PrecheckUncheckedComment),
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "A passed precheck has no decision."),
    };

    public static bool Climbs(TriageDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return decision.Verdict == TriageVerdict.NeedsReview && !_neverClimb.Contains(decision.Reason, StringComparer.Ordinal);
    }

    public static TriageDecision CiTimeoutDecision() => new(TriageVerdict.NeedsReview, CiTimeout, CiTimeoutComment);

    public static TriageDecision HighRiskDecision(string term)
    {
        ArgumentNullException.ThrowIfNull(term);
        return new TriageDecision(TriageVerdict.NeedsReview, HighRisk, $"{NeedsReviewPrefix} `{term}` is on the high-risk list. These always get a human look.");
    }

    public static TriageDecision JevErrorDecision() => new(TriageVerdict.NeedsReview, JevError, JevErrorComment);

    public static string ShortSha(string sha)
    {
        ArgumentNullException.ThrowIfNull(sha);
        return sha[..Math.Min(7, sha.Length)];
    }

    public static string StickyApprovalText(string headSha, string? approvalComment)
    {
        ArgumentNullException.ThrowIfNull(headSha);
        var reason = approvalComment is not null && approvalComment.StartsWith(AutoApprovedPrefix, StringComparison.Ordinal)
            ? approvalComment[AutoApprovedPrefix.Length..].Trim().TrimEnd('.')
            : string.Empty;
        var commit = $"{AutoApprovedPrefix} new commit `{ShortSha(headSha)}`";
        return reason.Length == 0 ? $"{commit}." : $"{commit}, {char.ToLowerInvariant(reason[0])}{reason[1..]}.";
    }

    private static TriageDecision DecidePatchOrMinor(string branch, JevAnswers jev, BumpFacts facts)
    {
        var disagreeing = facts.Versions.FirstOrDefault(v => !string.Equals(v.Kind, jev.BumpType, StringComparison.Ordinal));
        if (disagreeing is not null)
        {
            return new TriageDecision(
                TriageVerdict.NeedsReview,
                BumpTypeMismatch,
                $"{NeedsReviewPrefix} the versions in the diff (`{disagreeing.Old}` -> `{disagreeing.New}`) don't match the bump type Jev picked (`{jev.BumpType}`).");
        }

        if (facts.UnpairedVersions)
        {
            return new TriageDecision(TriageVerdict.NeedsReview, BumpTypeMismatch, $"{NeedsReviewPrefix} the versions in the diff don't match the bump type Jev picked (`{jev.BumpType}`).");
        }

        if (_allowlist.Any(entry => branch.StartsWith(entry, StringComparison.Ordinal)))
        {
            return new TriageDecision(TriageVerdict.Approve, Allowlisted, AllowlistedApprovalComment);
        }

        return jev.Changelog >= ChangelogThreshold
            ? new TriageDecision(TriageVerdict.Approve, ChangelogPresent, $"{AutoApprovedPrefix} {jev.BumpType} bump with changelog.")
            : new TriageDecision(TriageVerdict.NeedsReview, NoChangelog, NoChangelogComment);
    }

    private static bool IsCalendar(VersionChange change) => CalendarVersion().IsMatch(change.Old) && CalendarVersion().IsMatch(change.New);

    private static string VersionSuffix(VersionChange? change) => change is null ? string.Empty : $", `{change.Old}` -> `{change.New}`";

    private static string LinkText(string name)
    {
        var text = LineBreaks().Replace(name, " ").Replace("[", "(", StringComparison.Ordinal).Replace("]", ")", StringComparison.Ordinal).Replace("`", "'", StringComparison.Ordinal).Trim();
        return $"`{(text.Length == 0 ? "check" : text)}`";
    }

    private static List<GitHubCheckRun> ConsideredChecks(IReadOnlyList<GitHubCheckRun> checks) =>
        [.. checks.Where(c => !StartsWithAny(c.Name, _ignoredCheckPrefixes) && !string.Equals(c.AppSlug, MergifyApp, StringComparison.Ordinal))];

    private static bool IsFailing(GitHubCheckRun check) => !StartsWithAny(check.Name, _notFailingCheckPrefixes) && !PassingConclusion(check.Conclusion);

    private static bool IsCompleted(string status) => string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase);

    private static bool IsConsideredSuite(GitHubCheckSuite suite) =>
        !string.Equals(suite.AppSlug, MergifyApp, StringComparison.Ordinal)
        && (string.Equals(suite.AppSlug, GitHubActionsApp, StringComparison.Ordinal) || suite.CheckRunCount > 0);

    internal static bool IsRenovateCommit(GitHubCommit commit) =>
        string.Equals(commit.AuthorLogin, RenovateLogin, StringComparison.Ordinal)
        && commit.AuthorId == RenovateUserId
        && string.Equals(commit.AuthorEmail, RenovateAuthorEmail, StringComparison.Ordinal)
        && string.Equals(commit.CommitterEmail, GitHubCommitterEmail, StringComparison.Ordinal)
        && commit.Verified;

    private static bool IsBlockingLabel(string label, bool ignoreTriageSignals, bool honourNeedsReviewLabel, bool hasPendingRow) =>
        IsBlocking(label, hasPendingRow)
        && !(ignoreTriageSignals && _livePathLabels.Contains(label, StringComparer.Ordinal) && !(honourNeedsReviewLabel && label == NeedsReviewLabel));

    private static bool IsTriageBot(GitHubReview review) => string.Equals(review.AuthorLogin, TriageBotLogin, StringComparison.Ordinal);

    private static bool IsTrustedReviewer(GitHubReview review) =>
        IsTriageBot(review)
        || (review.AuthorAssociation is not null && _trustedAssociations.Contains(review.AuthorAssociation, StringComparer.Ordinal));

    private static bool PassingConclusion(string? conclusion) =>
        conclusion is not null && conclusion.ToLowerInvariant() is "success" or "neutral" or "skipped";

    private static bool StartsWithAny(string name, string[] prefixes) =>
        prefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal));

    [GeneratedRegex(@"^v?20\d\d\.", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CalendarVersion();

    [GeneratedRegex(@"\s*[\r\n]+\s*", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LineBreaks();

    [GeneratedRegex("kube|k8s|n8n|talos|ceph|rook|traefik|cilium|victoria|velero|postgres|litellm|flux|technitium|nexus|barman|app-template|cnpg|cloudflared|vaultwarden|authentik|dns|foundryvtt", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RiskyBranch();
}
