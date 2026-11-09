using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace AgentPlatform.Host.Livemap;

public enum LivemapReleaseCheckOutcome
{
    Verified,
    Pending,
    Failed,
}

// Pending means a fact is not there yet and may still arrive; Failed means the release is final and wrong.
public sealed record LivemapReleaseCheck(LivemapReleaseCheckOutcome Outcome, string? Sha256, string? Reason)
{
    public static LivemapReleaseCheck Verified(string sha256) => new(LivemapReleaseCheckOutcome.Verified, sha256, null);

    public static LivemapReleaseCheck Pending(string reason) => new(LivemapReleaseCheckOutcome.Pending, null, reason);

    public static LivemapReleaseCheck Failed(string reason) => new(LivemapReleaseCheckOutcome.Failed, null, reason);
}

public sealed partial class LivemapReleaseVerifier(ILivemapReleases releases, ILivemapGitHub github, IOptions<LivemapOptions> options)
{
    private const string ReleaseWorkflowPath = ".github/workflows/release.yaml";

    private const int MaxChecksumChars = 1024;

    public async Task<LivemapRelease?> FindReleaseWithBuildAsync(long buildId, CancellationToken cancellationToken)
    {
        var latest = await releases.GetLatestAsync(cancellationToken).ConfigureAwait(false);
        if (latest is null || !LivemapPin.IsReleaseTag(latest.Tag))
        {
            return null;
        }

        var commit = await releases.GetTagCommitAsync(latest.Tag, cancellationToken).ConfigureAwait(false);
        return commit is not null && await BuildListProblemAsync(commit, buildId, cancellationToken).ConfigureAwait(false) is null ? latest : null;
    }

    public async Task<LivemapReleaseCheck> VerifyAsync(string tag, long buildId, CancellationToken cancellationToken)
    {
        if (!LivemapPin.IsReleaseTag(tag))
        {
            throw new ArgumentException("The tag must be a plain release tag.", nameof(tag));
        }

        var release = await releases.GetByTagAsync(tag, cancellationToken).ConfigureAwait(false);
        if (release is null || release.Draft)
        {
            return LivemapReleaseCheck.Pending("The release is not published yet.");
        }

        if (!release.Immutable)
        {
            return LivemapReleaseCheck.Failed("The release is not immutable.");
        }

        var zipName = $"EnshroudedLiveMap-{tag[1..]}.zip";
        var checksumName = zipName + ".sha256";
        var zip = release.Assets.FirstOrDefault(asset => asset.Name == zipName);
        var checksum = release.Assets.FirstOrDefault(asset => asset.Name == checksumName);
        if (zip is null || checksum is null)
        {
            return LivemapReleaseCheck.Failed($"The release is missing {(zip is null ? zipName : checksumName)}.");
        }

        if (zip.Digest is null || !ZipDigest().IsMatch(zip.Digest))
        {
            return LivemapReleaseCheck.Failed($"{zipName} has no sha256 digest.");
        }

        var sha256 = zip.Digest["sha256:".Length..];
        var text = await releases.ReadAssetTextAsync(checksum.Id, MaxChecksumChars, cancellationToken).ConfigureAwait(false);
        if (FirstField(text) != sha256)
        {
            return LivemapReleaseCheck.Failed($"{checksumName} does not match the digest of {zipName}.");
        }

        var provenance = await releases.GetProvenanceAsync(sha256, cancellationToken).ConfigureAwait(false);
        if (provenance.Count == 0)
        {
            return LivemapReleaseCheck.Pending("The release has no build provenance yet.");
        }

        var commit = await releases.GetTagCommitAsync(tag, cancellationToken).ConfigureAwait(false);
        if (commit is null)
        {
            return LivemapReleaseCheck.Failed("The release tag has no commit.");
        }

        var repository = $"https://github.com/{options.Value.Repo}";
        if (!provenance.Any(statement => Matches(statement, zipName, sha256, tag, repository, commit)))
        {
            return LivemapReleaseCheck.Failed($"No build provenance ties {zipName} to the release workflow run on {tag}.");
        }

        return await BuildListProblemAsync(commit, buildId, cancellationToken).ConfigureAwait(false) is { } problem
            ? LivemapReleaseCheck.Failed(problem)
            : LivemapReleaseCheck.Verified(sha256);
    }

    private static bool Matches(LivemapProvenance statement, string zipName, string sha256, string tag, string repository, string commit) =>
        statement.Subjects.Any(subject => subject.Name == zipName && subject.Sha256 == sha256)
        && statement.WorkflowPath == ReleaseWorkflowPath
        && statement.WorkflowRef == $"refs/tags/{tag}"
        && statement.Repository == repository
        && statement.CommitSha == commit;

    private static string? FirstField(string? text) =>
        text?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

    private async Task<string?> BuildListProblemAsync(string commit, long buildId, CancellationToken cancellationToken)
    {
        var builds = await github.ReadBuildsJsonAsync(commit, cancellationToken).ConfigureAwait(false);
        if (builds is null)
        {
            return "The release has no build list.";
        }

        try
        {
            return ShippedBuildsReader.ParseBuildIds(builds).Contains(buildId) ? null : "The release does not list the build.";
        }
        catch (ShippedBuildsException)
        {
            return "The release build list cannot be read.";
        }
    }

    [GeneratedRegex(@"^sha256:[a-f0-9]{64}\z")]
    private static partial Regex ZipDigest();
}
