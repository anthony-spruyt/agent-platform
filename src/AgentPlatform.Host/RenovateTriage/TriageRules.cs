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

    public const double BreakingThreshold = 0.66;
    public const double ChangelogThreshold = 0.7;

    public const string NotRenovate = "not-renovate";
    public const string BlockingLabel = "blocking-label";
    public const string AlreadyApproved = "already-approved";
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

    public const string CiFailedComment = "Flagged for review as CI failed.";
    public const string HighRiskComment = "This is a high risk dependency bump and requires review.";
    public const string BreakingComment = "This dependency bump has breaking changes and requires review.";
    public const string NoChangelogComment = "This dependency bump has no changelog and requires review.";

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

    public static bool IsApproved(IReadOnlyList<GitHubReview> reviews)
    {
        ArgumentNullException.ThrowIfNull(reviews);
        var latest = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var review in reviews.Where(r => !string.Equals(r.State, "COMMENTED", StringComparison.OrdinalIgnoreCase)))
        {
            latest[review.AuthorLogin ?? string.Empty] = review.State.ToUpperInvariant();
        }

        if (latest.Values.Contains("CHANGES_REQUESTED", StringComparer.Ordinal))
        {
            return false;
        }

        return latest.Values.Contains("APPROVED", StringComparer.Ordinal);
    }

    public static CiState AssessChecks(IReadOnlyList<GitHubCheckRun> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        var considered = checks.Where(c => !StartsWithAny(c.Name, _ignoredCheckPrefixes)).ToList();
        if (considered.Any(c => !string.Equals(c.Status, "completed", StringComparison.OrdinalIgnoreCase)))
        {
            return CiState.Pending;
        }

        var failed = considered.Any(c => !StartsWithAny(c.Name, _notFailingCheckPrefixes) && !PassingConclusion(c.Conclusion));
        return failed ? CiState.Failed : CiState.Passed;
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

    public static TriageDecision CiFailedDecision() => new(TriageVerdict.NeedsReview, CiFailed, CiFailedComment);

    public static TriageDecision CiTimeoutDecision() => new(TriageVerdict.NeedsReview, CiTimeout, null);

    public static TriageDecision HighRiskDecision() => new(TriageVerdict.NeedsReview, HighRisk, HighRiskComment);

    public static TriageDecision JevErrorDecision() => new(TriageVerdict.NeedsReview, JevError, null);

    private static TriageDecision DecidePatchOrMinor(string branch, JevAnswers jev)
    {
        if (_allowlist.Any(entry => branch.StartsWith(entry, StringComparison.OrdinalIgnoreCase)))
        {
            return new TriageDecision(TriageVerdict.Approve, Allowlisted, null);
        }

        return jev.Changelog >= ChangelogThreshold
            ? new TriageDecision(TriageVerdict.Approve, ChangelogPresent, null)
            : new TriageDecision(TriageVerdict.NeedsReview, NoChangelog, NoChangelogComment);
    }

    private static bool PassingConclusion(string? conclusion) =>
        conclusion is not null && conclusion.ToLowerInvariant() is "success" or "neutral" or "skipped";

    private static bool StartsWithAny(string name, string[] prefixes) =>
        prefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal));

    [GeneratedRegex("kube|k8s|n8n|talos|ceph|rook|traefik|cilium|victoria|velero|postgres|litellm|flux|technitium|nexus|barman|app-template|cnpg|cloudflared|vaultwarden|authentik|dns|foundryvtt", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RiskyBranch();
}
