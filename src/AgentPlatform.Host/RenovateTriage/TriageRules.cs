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

    public static string? SkipReason(long? authorId, IReadOnlyCollection<string> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        if (authorId != RenovateUserId)
        {
            return NotRenovate;
        }

        return labels.Any(l => _blockingLabels.Contains(l, StringComparer.Ordinal)) ? BlockingLabel : null;
    }

    public static bool IsSameRepo(string repo, string? headRepo, string? baseRepo)
    {
        ArgumentNullException.ThrowIfNull(repo);
        return string.Equals(headRepo, repo, StringComparison.OrdinalIgnoreCase) && string.Equals(baseRepo, repo, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsApproved(IReadOnlyList<GitHubReview> reviews, string headSha)
    {
        ArgumentNullException.ThrowIfNull(reviews);
        ArgumentNullException.ThrowIfNull(headSha);
        var latest = new Dictionary<string, GitHubReview>(StringComparer.Ordinal);
        foreach (var review in reviews.Where(r => IsTrustedReviewer(r) && !string.Equals(r.State, "COMMENTED", StringComparison.OrdinalIgnoreCase)))
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

    public static bool AreAllRenovateCommits(IReadOnlyList<GitHubCommit> commits)
    {
        ArgumentNullException.ThrowIfNull(commits);
        return commits.Count > 0 && commits.All(IsRenovateCommit);
    }

    public static string? UpdateTypeFromBody(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return null;
        }

        var updateColumn = -1;
        string? strictest = null;
        foreach (var line in body.Split('\n').Select(l => l.Trim()))
        {
            if (line == "---")
            {
                break;
            }

            if (updateColumn < 0)
            {
                updateColumn = UpdateColumn(line);
                continue;
            }

            if (!line.StartsWith('|'))
            {
                break;
            }

            var cells = Cells(line);
            if (cells.All(c => TableSeparatorCell().IsMatch(c)))
            {
                continue;
            }

            var type = updateColumn < cells.Count ? MapUpdateKind(cells[updateColumn]) : null;
            if (type is null)
            {
                return null;
            }

            strictest = strictest is null ? type : ResolveBump(strictest, type);
        }

        return strictest;
    }

    public static string ResolveBump(string codeType, string jevBump)
    {
        ArgumentNullException.ThrowIfNull(codeType);
        ArgumentNullException.ThrowIfNull(jevBump);
        return BumpRank(jevBump) >= BumpRank(codeType) ? jevBump : codeType;
    }

    public static bool IsRiskyBranch(string branch)
    {
        ArgumentNullException.ThrowIfNull(branch);
        var lower = branch.ToLowerInvariant();
        return RiskyBranch().IsMatch(lower) && !lower.Contains("n8n-mcp", StringComparison.Ordinal);
    }

    public static TriageDecision DecideFromJev(string branch, JevAnswers jev, string codeType)
    {
        ArgumentNullException.ThrowIfNull(branch);
        ArgumentNullException.ThrowIfNull(jev);
        if (jev.Breaking >= BreakingThreshold)
        {
            return new TriageDecision(TriageVerdict.NeedsReview, Breaking, BreakingComment);
        }

        switch (ResolveBump(codeType, jev.BumpType))
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

    public static TriageDecision UnknownBumpDecision() => new(TriageVerdict.NeedsReview, UnknownBump, null);

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

    private static List<string> Cells(string row) =>
        [.. UnescapedPipe().Split(row.Trim().Trim('|')).Select(c => c.Trim())];

    private static int UpdateColumn(string line)
    {
        if (!line.StartsWith('|'))
        {
            return -1;
        }

        var cells = Cells(line);
        return cells.Exists(c => string.Equals(c, "Package", StringComparison.OrdinalIgnoreCase))
            ? cells.FindIndex(c => string.Equals(c, "Update", StringComparison.OrdinalIgnoreCase))
            : -1;
    }

    private static string? MapUpdateKind(string kind) => kind.ToLowerInvariant() switch
    {
        "major" => "major",
        "minor" => "minor",
        "patch" => "patch",
        "digest" or "pin" or "pindigest" => "digest",
        _ => null,
    };

    private static int BumpRank(string bump) => bump switch
    {
        "digest" => 0,
        "patch" => 1,
        "minor" => 2,
        "major" => 3,
        _ => 4,
    };

    private static bool IsTrustedReviewer(GitHubReview review) =>
        string.Equals(review.AuthorLogin, TriageBotLogin, StringComparison.Ordinal)
        || (review.AuthorAssociation is not null && _trustedAssociations.Contains(review.AuthorAssociation, StringComparer.Ordinal));

    private static bool PassingConclusion(string? conclusion) =>
        conclusion is not null && conclusion.ToLowerInvariant() is "success" or "neutral" or "skipped";

    private static bool StartsWithAny(string name, string[] prefixes) =>
        prefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal));

    [GeneratedRegex(@"(?<!\\)\|", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex UnescapedPipe();

    [GeneratedRegex("^:?-+:?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TableSeparatorCell();

    [GeneratedRegex("kube|k8s|n8n|talos|ceph|rook|traefik|cilium|victoria|velero|postgres|litellm|flux|technitium|nexus|barman|app-template|cnpg|cloudflared|vaultwarden|authentik|dns|foundryvtt", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RiskyBranch();
}
