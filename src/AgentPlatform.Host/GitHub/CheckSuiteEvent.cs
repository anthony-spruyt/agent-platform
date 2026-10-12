using System.Text.Json;
using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Host.Workflows;

namespace AgentPlatform.Host.GitHub;

public sealed record CheckSuiteEvent(string? Action, CheckSuiteEvent.Suite? CheckSuite, CheckSuiteEvent.RepositoryInfo? Repository)
{
    private const string RenovateBranchPrefix = "renovate/";
    private const string RenovateOnboardingBranch = "renovate/configure";

    public sealed record Suite(long Id, string? HeadBranch, string? HeadSha, string? Conclusion, AppInfo? App, IReadOnlyList<PullRequestInfo>? PullRequests);

    public sealed record AppInfo(string? Slug);

    public sealed record PullRequestInfo(int Number, HeadInfo? Head, BaseInfo? Base);

    public sealed record HeadInfo(HeadRepositoryInfo? Repo);

    public sealed record BaseInfo(HeadRepositoryInfo? Repo);

    public sealed record HeadRepositoryInfo(long Id);

    public sealed record RepositoryInfo(long Id, string? FullName);

    public static IReadOnlyList<CheckSuiteCompletedSignal> ToSignals(ReadOnlySpan<byte> body, string deliveryId)
    {
        ArgumentNullException.ThrowIfNull(deliveryId);
        var payload = JsonSerializer.Deserialize<CheckSuiteEvent>(body, GitHubJson.Options);
        return payload?.ToSignals(deliveryId) ?? [];
    }

    private List<CheckSuiteCompletedSignal> ToSignals(string deliveryId)
    {
        var signals = new List<CheckSuiteCompletedSignal>();
        if (Action != "completed"
            || CheckSuite is not { HeadSha.Length: > 0, PullRequests: { Count: > 0 } pullRequests } suite
            || Repository is not { Id: > 0, FullName: { } fullName } repository
            || !GitHubRepoName.TryParse(fullName, out _, out _)
            || !TriageRules.IsAllowedOwner(fullName)
            || !IsRenovateUpdateBranch(suite.HeadBranch))
        {
            return signals;
        }

        foreach (var pullRequest in pullRequests)
        {
            if (pullRequest is { Number: > 0, Head.Repo: { } headRepo, Base.Repo: { } baseRepo }
                && headRepo.Id == repository.Id
                && baseRepo.Id == repository.Id)
            {
                signals.Add(new CheckSuiteCompletedSignal(
                    fullName,
                    pullRequest.Number,
                    suite.HeadSha,
                    suite.Id,
                    suite.App?.Slug,
                    suite.Conclusion,
                    deliveryId));
            }
        }

        return signals;
    }

    private static bool IsRenovateUpdateBranch(string? branch) =>
        branch is not null
        && branch.StartsWith(RenovateBranchPrefix, StringComparison.Ordinal)
        && branch != RenovateOnboardingBranch;
}
