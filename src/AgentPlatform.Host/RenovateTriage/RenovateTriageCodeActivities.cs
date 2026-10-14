using System.Net;
using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.PullRequests;
using Microsoft.Extensions.Options;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace AgentPlatform.Host.RenovateTriage;

public sealed record ScannedChangelog(string Dependency, string Origin, ScanOutcome Outcome, ScannedText Text);

public sealed record FetchChangelogInput(string Repo, int PrNumber, string HeadSha, ScannedText PrBody);

public sealed record FetchChangelogResult(bool HeadMoved, IReadOnlyList<ScannedChangelog> Sections);

public sealed record DetectMigrationInput(string Repo, int PrNumber, string HeadSha, ScannedText PrBody);

public sealed record DetectMigrationResult(bool HeadMoved, TriageDecision? Decision);

public sealed record CiCodeInput(string Repo, int PrNumber, string HeadSha, bool AfterRerun = false);

internal sealed record FailingJobLog(string Job, string Tail);

public sealed record ClassifyCiResult(bool HeadMoved, CiFailureKind Kind, int FailingJobs, string? JevError = null, bool Stale = false);

public sealed record RerunResult(bool HeadMoved, RerunOutcome Outcome);

public sealed record BuildContextInput(string Repo, int PrNumber, string HeadSha, string? Reason, ScannedText? PrBody = null);

public sealed record BuildContextResult(bool HeadMoved, TriageContext? Context, StatedVersions? Stated = null);

public sealed class RenovateTriageCodeActivities(IGitHubApiClient github, IJevClient jev, IUntrustedTextScanner scanner, IOptions<RenovateTriageOptions> options)
{
    public const string PrecheckName = "RenovateTriagePrecheck";
    public const string BuildContextName = "RenovateTriageBuildContext";
    public const string FetchChangelogName = "RenovateTriageFetchChangelog";
    public const string DetectMigrationName = "RenovateTriageDetectMigration";
    public const string ClassifyCiName = "RenovateTriageClassifyCi";
    public const string RerunFailedJobsName = "RenovateTriageRerunFailedJobs";

    public const int MaxJobLogChars = 30_000;
    public const string LogUnavailable = "(the job log is no longer available)";

    private const int MaxFailingJobs = 10;
    private const int MaxJobNameChars = 100;
    private const int MinJevJobShare = 300;
    private const string GitHubActionsApp = "github-actions";

    private readonly ChangelogFetcher _changelogs = new(github);

    [Activity(PrecheckName)]
    public Task<PrecheckResult> PrecheckAsync(PrecheckInput input) =>
        GuardAsync(async () =>
        {
            Validate(input.Repo, input.PrNumber);
            var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
            var pr = await github.GetPullRequestAsync(input.Repo, input.PrNumber, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(pr.HeadSha, input.HeadSha, StringComparison.Ordinal))
            {
                return new PrecheckResult(true, PrecheckVerdict.Passed);
            }

            return await TriagePrecheck.RunAsync(github, scanner, input.Repo, input.PrNumber, pr, cancellationToken).ConfigureAwait(false);
        });

    [Activity(BuildContextName)]
    public Task<BuildContextResult> BuildContextAsync(BuildContextInput input) =>
        GuardAsync(async () =>
        {
            Validate(input.Repo, input.PrNumber);
            var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
            var pr = await github.GetPullRequestAsync(input.Repo, input.PrNumber, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(pr.HeadSha, input.HeadSha, StringComparison.Ordinal))
            {
                return new BuildContextResult(true, null);
            }

            var bundle = new Bundle();
            ScannedText body;
            var bodyClean = true;
            if (input.PrBody is null)
            {
                var scan = await scanner.ScanAsync(pr.Body ?? string.Empty, cancellationToken).ConfigureAwait(false);
                body = scan.Text;
                bodyClean = scan.Outcome == ScanOutcome.Clean;
                bundle.Add("pr-description", "Renovate pull request description", scan.Outcome, body);
            }
            else
            {
                RequireIntact(input.PrBody);
                body = input.PrBody;
                bundle.Add("pr-description", "Renovate pull request description", ScanOutcome.Clean, body);
            }

            if (bodyClean)
            {
                foreach (var section in await ScanChangelogsAsync(input.Repo, body.Text, cancellationToken).ConfigureAwait(false))
                {
                    bundle.Add("changelog", $"{section.Dependency} ({section.Origin})", section.Outcome, section.Text);
                }
            }

            var checks = await github.ListCheckRunsAsync(input.Repo, input.HeadSha, cancellationToken).ConfigureAwait(false);
            var failing = await FailingJobsAsync(input.Repo, checks, cancellationToken).ConfigureAwait(false);
            var jobs = await JobLogsAsync(input.Repo, failing, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < jobs.Count; i++)
            {
                var scan = await scanner.ScanAsync($"CI job: {jobs[i].Job}\n{jobs[i].Tail}", cancellationToken).ConfigureAwait(false);
                bundle.Add("ci-log", $"CI job {i + 1}", scan.Outcome, scan.Text);
            }

            var facts = new TriageFacts(input.Repo, input.PrNumber, input.HeadSha, input.Reason, jobs.Count, bundle.HeldBack);
            var stated = await StatedVersionsAsync(input.Repo, input.PrNumber, input.HeadSha, cancellationToken).ConfigureAwait(false);
            return new BuildContextResult(false, TriageContextBuilder.Build(facts, bundle.Items), stated);
        });

    [Activity(FetchChangelogName)]
    public Task<FetchChangelogResult> FetchChangelogAsync(FetchChangelogInput input) =>
        GuardAsync(async () =>
        {
            Validate(input.Repo, input.PrNumber);
            var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
            if (await HeadMovedAsync(input.Repo, input.PrNumber, input.HeadSha, cancellationToken).ConfigureAwait(false))
            {
                return new FetchChangelogResult(true, []);
            }

            RequireIntact(input.PrBody);
            return new FetchChangelogResult(false, await ScanChangelogsAsync(input.Repo, input.PrBody.Text, cancellationToken).ConfigureAwait(false));
        });

    [Activity(DetectMigrationName)]
    public Task<DetectMigrationResult> DetectMigrationAsync(DetectMigrationInput input) =>
        GuardAsync(async () =>
        {
            Validate(input.Repo, input.PrNumber);
            var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
            if (await HeadMovedAsync(input.Repo, input.PrNumber, input.HeadSha, cancellationToken).ConfigureAwait(false))
            {
                return new DetectMigrationResult(true, null);
            }

            RequireIntact(input.PrBody);
            var files = await github.ListPullRequestFilesAsync(input.Repo, input.PrNumber, cancellationToken).ConfigureAwait(false);
            var dependencies = RenovateBodyTable.Parse(input.PrBody.Text).Select(u => u.Dependency).ToList();
            var match = new MigrationDetector(options.Value).Detect([.. files.Select(f => f.Filename)], dependencies);
            return new DetectMigrationResult(false, match is null ? null : MigrationDetector.Decision(match));
        });

    [Activity(ClassifyCiName)]
    public Task<ClassifyCiResult> ClassifyCiAsync(CiCodeInput input) =>
        GuardAsync(async () =>
        {
            Validate(input.Repo, input.PrNumber);
            var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
            if (await HeadMovedAsync(input.Repo, input.PrNumber, input.HeadSha, cancellationToken).ConfigureAwait(false))
            {
                return new ClassifyCiResult(true, CiFailureKind.Unsure, 0);
            }

            var checks = await github.ListCheckRunsAsync(input.Repo, input.HeadSha, cancellationToken).ConfigureAwait(false);
            if (input.AfterRerun)
            {
                var suites = await github.ListCheckSuitesAsync(input.Repo, input.HeadSha, cancellationToken).ConfigureAwait(false);
                if (TriageRules.AssessChecks(checks, suites) != CiState.Failed)
                {
                    return new ClassifyCiResult(false, CiFailureKind.Unsure, 0, Stale: true);
                }
            }

            var failing = await FailingJobsAsync(input.Repo, checks, cancellationToken).ConfigureAwait(false);
            if (input.AfterRerun && CiRerunPolicy.AwaitsNewAttempt(failing))
            {
                return new ClassifyCiResult(false, CiFailureKind.Unsure, failing.Count, Stale: true);
            }

            var logs = await JobLogsAsync(input.Repo, failing, cancellationToken).ConfigureAwait(false);
            if (logs.Count == 0)
            {
                return new ClassifyCiResult(false, CiFailureKind.Unsure, 0);
            }

            try
            {
                var kind = await jev.ClassifyCiAsync(JevInput(logs), cancellationToken).ConfigureAwait(false);
                return new ClassifyCiResult(false, kind, logs.Count);
            }
            catch (JevException ex)
            {
                return new ClassifyCiResult(false, CiFailureKind.Unsure, logs.Count, ex.Message);
            }
        });

    [Activity(RerunFailedJobsName)]
    public Task<RerunResult> RerunFailedJobsAsync(CiCodeInput input) =>
        GuardAsync(async () =>
        {
            Validate(input.Repo, input.PrNumber);
            var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
            if (await HeadMovedAsync(input.Repo, input.PrNumber, input.HeadSha, cancellationToken).ConfigureAwait(false))
            {
                return new RerunResult(true, RerunOutcome.NothingFailed);
            }

            var checks = await github.ListCheckRunsAsync(input.Repo, input.HeadSha, cancellationToken).ConfigureAwait(false);
            var jobs = await FailingJobsAsync(input.Repo, checks, cancellationToken).ConfigureAwait(false);
            var plan = CiRerunPolicy.Decide(jobs);
            foreach (var runId in plan.RunIds)
            {
                await github.RerunFailedJobsAsync(input.Repo, runId, cancellationToken).ConfigureAwait(false);
            }

            return new RerunResult(false, plan.Outcome);
        });

    private static string JevInput(IReadOnlyList<FailingJobLog> logs)
    {
        var share = Math.Max(MinJevJobShare, JevClient.MaxCiLogChars / logs.Count);
        return string.Join("\n\n", logs.Select(l =>
        {
            var header = $"### {l.Job}\n";
            var room = Math.Max(0, share - header.Length - 2);
            return header + (l.Tail.Length <= room ? l.Tail : l.Tail[^room..]);
        }));
    }

    private static string Trim(string name) => name.Length <= MaxJobNameChars ? name : name[..MaxJobNameChars];

    private static void Validate(string repo, int prNumber)
    {
        GitHubRepoName.Parse(repo, nameof(repo));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(prNumber);
    }

    private static async Task<T> GuardAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            throw new ApplicationFailureException(ex.Message, ex, nonRetryable: true);
        }
        catch (GitHubApiException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.UnprocessableEntity)
        {
            throw new ApplicationFailureException(ex.Message, ex, nonRetryable: true);
        }
    }

    private static void RequireIntact(ScannedText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.IsIntact)
        {
            throw new ArgumentException("A scanned text no longer matches the hash it was scanned under.");
        }
    }

    private async Task<IReadOnlyList<ScannedChangelog>> ScanChangelogsAsync(string repo, string body, CancellationToken cancellationToken)
    {
        var sections = await _changelogs.FetchAsync(repo, RenovateBodyTable.Parse(body), cancellationToken).ConfigureAwait(false);
        var scanned = new List<ScannedChangelog>();
        foreach (var section in sections)
        {
            var scan = await scanner.ScanAsync(section.Text, cancellationToken).ConfigureAwait(false);
            scanned.Add(new ScannedChangelog(section.Dependency, section.Origin, scan.Outcome, scan.Text));
        }

        return scanned;
    }

    private async Task<IReadOnlyList<FailingJobLog>> JobLogsAsync(string repo, IReadOnlyList<GitHubJob> failing, CancellationToken cancellationToken)
    {
        var logs = new List<FailingJobLog>();
        foreach (var job in failing)
        {
            var tail = await github.GetJobLogTailAsync(repo, job.Id, MaxJobLogChars, cancellationToken).ConfigureAwait(false);
            logs.Add(new FailingJobLog(Trim(job.Name), tail ?? LogUnavailable));
        }

        return logs;
    }

    private sealed class Bundle
    {
        public List<BundleItem> Items { get; } = [];

        public int HeldBack { get; private set; }

        public void Add(string kind, string title, ScanOutcome outcome, ScannedText text)
        {
            if (outcome == ScanOutcome.Clean)
            {
                Items.Add(new BundleItem(kind, title, text));
            }
            else
            {
                HeldBack++;
            }
        }
    }

    private async Task<StatedVersions> StatedVersionsAsync(string repo, int prNumber, string headSha, CancellationToken cancellationToken)
    {
        var commits = await github.ListCommitsAsync(repo, prNumber, cancellationToken).ConfigureAwait(false);
        var unreadable = new StatedVersions(null, null);
        return commits.Count > 0 && string.Equals(commits[^1].Sha, headSha, StringComparison.OrdinalIgnoreCase)
            ? DiffShape.ReadStatedVersions(commits[^1].Message) ?? unreadable
            : unreadable;
    }

    private async Task<bool> HeadMovedAsync(string repo, int prNumber, string headSha, CancellationToken cancellationToken)
    {
        var pr = await github.GetPullRequestAsync(repo, prNumber, cancellationToken).ConfigureAwait(false);
        return !string.Equals(pr.HeadSha, headSha, StringComparison.Ordinal);
    }

    private async Task<IReadOnlyList<GitHubJob>> FailingJobsAsync(string repo, IReadOnlyList<GitHubCheckRun> checks, CancellationToken cancellationToken)
    {
        var jobs = new List<GitHubJob>();
        foreach (var check in TriageRules.FailedChecks(checks).Where(c => string.Equals(c.AppSlug, GitHubActionsApp, StringComparison.Ordinal)).Take(MaxFailingJobs))
        {
            if (await github.GetJobAsync(repo, check.Id, cancellationToken).ConfigureAwait(false) is { } job)
            {
                jobs.Add(job);
            }
        }

        return jobs;
    }
}
