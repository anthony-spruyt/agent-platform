using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AgentPlatform.Host.GitHub;

namespace AgentPlatform.Host.RenovateTriage;

[JsonConverter(typeof(JsonStringEnumConverter<TriageVerdict>))]
public enum TriageVerdict
{
    Approve,
    NeedsReview,
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

    public const string NotOpen = "not-open";
    public const string ForeignRepo = "foreign-repo";
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

    public const string NonRenovateCommitComment = "This dependency bump has commits not made by Renovate and requires review.";
    public const string CiFailedComment = "Flagged for review as CI failed.";
    public const string HighRiskComment = "This is a high risk dependency bump and requires review.";
    public const string BreakingComment = "This dependency bump has breaking changes and requires review.";
    public const string NoChangelogComment = "This dependency bump has no changelog and requires review.";

    private const string RenovateLogin = "renovate[bot]";
    private const string RenovateAuthorEmail = "29139614+renovate[bot]@users.noreply.github.com";
    private const string GitHubCommitterEmail = "noreply@github.com";
    private const string GitHubActionsApp = "github-actions";
    private const string MergifyApp = "mergify";

    private static readonly string[] _trustedAssociations = ["OWNER", "MEMBER", "COLLABORATOR"];

    private static readonly string[] _blockingLabels = ["agent/needs-review", "agent/revert", "blocked"];

    private static readonly string[] _livePathLabels = ["agent/needs-review", "agent/auto-approved"];

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

    public static string? SkipReason(long? authorId, IReadOnlyCollection<string> labels, bool shadow = false)
    {
        ArgumentNullException.ThrowIfNull(labels);
        if (authorId != RenovateUserId)
        {
            return NotRenovate;
        }

        return labels.Any(l => IsBlockingLabel(l, shadow)) ? BlockingLabel : null;
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

    public static bool IsApproved(IReadOnlyList<GitHubReview> reviews, string headSha, bool shadow = false)
    {
        ArgumentNullException.ThrowIfNull(reviews);
        ArgumentNullException.ThrowIfNull(headSha);
        var latest = new Dictionary<string, GitHubReview>(StringComparer.Ordinal);
        foreach (var review in reviews.Where(r => !(shadow && IsTriageBot(r)) && IsTrustedReviewer(r) && !string.Equals(r.State, "COMMENTED", StringComparison.OrdinalIgnoreCase)))
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

    public static CiState AssessChecks(IReadOnlyList<GitHubCheckRun> checks, IReadOnlyList<GitHubCheckSuite> suites)
    {
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(suites);
        var considered = checks
            .Where(c => !StartsWithAny(c.Name, _ignoredCheckPrefixes) && !string.Equals(c.AppSlug, MergifyApp, StringComparison.Ordinal))
            .ToList();
        var consideredSuites = suites.Where(IsConsideredSuite);
        if (considered.Count == 0
            || considered.Any(c => !IsCompleted(c.Status))
            || consideredSuites.Any(s => !IsCompleted(s.Status)))
        {
            return CiState.Pending;
        }

        var failed = considered.Any(c => !StartsWithAny(c.Name, _notFailingCheckPrefixes) && !PassingConclusion(c.Conclusion));
        return failed ? CiState.Failed : CiState.Passed;
    }

    public static bool AreAllRenovateCommits(IReadOnlyList<GitHubCommit> commits, int prCommitCount)
    {
        ArgumentNullException.ThrowIfNull(commits);
        return prCommitCount is > 0 and < MaxListedCommits && commits.Count == prCommitCount && commits.All(IsRenovateCommit);
    }

    public static bool IsRiskyBranch(string branch)
    {
        ArgumentNullException.ThrowIfNull(branch);
        var lower = branch.ToLowerInvariant();
        return RiskyBranch().IsMatch(lower) && !lower.Contains("n8n-mcp", StringComparison.Ordinal);
    }

    public static TriageDecision DecideFromJev(string branch, JevAnswers jev)
    {
        ArgumentNullException.ThrowIfNull(branch);
        ArgumentNullException.ThrowIfNull(jev);
        if (jev.Breaking >= BreakingThreshold)
        {
            return new TriageDecision(TriageVerdict.NeedsReview, Breaking, BreakingComment);
        }

        switch (jev.BumpType)
        {
            case "digest":
                return new TriageDecision(TriageVerdict.Approve, Digest, null);
            case "major":
                return new TriageDecision(TriageVerdict.NeedsReview, MajorBump, BreakingComment);
            case "calver":
                return new TriageDecision(TriageVerdict.NeedsReview, CalverBump, null);
            case "other":
                return new TriageDecision(TriageVerdict.NeedsReview, OtherBump, null);
            case "patch":
            case "minor":
                return DecidePatchOrMinor(branch, jev);
            default:
                return new TriageDecision(TriageVerdict.NeedsReview, UnknownBump, null);
        }
    }

    public static TriageDecision NonRenovateCommitDecision() => new(TriageVerdict.NeedsReview, NonRenovateCommit, NonRenovateCommitComment);

    public static TriageDecision CiFailedDecision() => new(TriageVerdict.NeedsReview, CiFailed, CiFailedComment);

    public static TriageDecision CiTimeoutDecision() => new(TriageVerdict.NeedsReview, CiTimeout, null);

    public static TriageDecision HighRiskDecision() => new(TriageVerdict.NeedsReview, HighRisk, HighRiskComment);

    public static TriageDecision JevErrorDecision() => new(TriageVerdict.NeedsReview, JevError, null);

    private static TriageDecision DecidePatchOrMinor(string branch, JevAnswers jev)
    {
        if (_allowlist.Any(entry => branch.StartsWith(entry, StringComparison.Ordinal)))
        {
            return new TriageDecision(TriageVerdict.Approve, Allowlisted, null);
        }

        return jev.Changelog >= ChangelogThreshold
            ? new TriageDecision(TriageVerdict.Approve, ChangelogPresent, null)
            : new TriageDecision(TriageVerdict.NeedsReview, NoChangelog, NoChangelogComment);
    }

    private static bool IsCompleted(string status) => string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase);

    private static bool IsConsideredSuite(GitHubCheckSuite suite) =>
        !string.Equals(suite.AppSlug, MergifyApp, StringComparison.Ordinal)
        && (string.Equals(suite.AppSlug, GitHubActionsApp, StringComparison.Ordinal) || suite.CheckRunCount > 0);

    private static bool IsRenovateCommit(GitHubCommit commit) =>
        string.Equals(commit.AuthorLogin, RenovateLogin, StringComparison.Ordinal)
        && commit.AuthorId == RenovateUserId
        && string.Equals(commit.AuthorEmail, RenovateAuthorEmail, StringComparison.Ordinal)
        && string.Equals(commit.CommitterEmail, GitHubCommitterEmail, StringComparison.Ordinal)
        && commit.Verified;

    private static bool IsBlockingLabel(string label, bool shadow) =>
        _blockingLabels.Contains(label, StringComparer.Ordinal) && !(shadow && _livePathLabels.Contains(label, StringComparer.Ordinal));

    private static bool IsTriageBot(GitHubReview review) => string.Equals(review.AuthorLogin, TriageBotLogin, StringComparison.Ordinal);

    private static bool IsTrustedReviewer(GitHubReview review) =>
        IsTriageBot(review)
        || (review.AuthorAssociation is not null && _trustedAssociations.Contains(review.AuthorAssociation, StringComparer.Ordinal));

    private static bool PassingConclusion(string? conclusion) =>
        conclusion is not null && conclusion.ToLowerInvariant() is "success" or "neutral" or "skipped";

    private static bool StartsWithAny(string name, string[] prefixes) =>
        prefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal));

    [GeneratedRegex("kube|k8s|n8n|talos|ceph|rook|traefik|cilium|victoria|velero|postgres|litellm|flux|technitium|nexus|barman|app-template|cnpg|cloudflared|vaultwarden|authentik|dns|foundryvtt", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RiskyBranch();
}
