using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Host.Workflows;

namespace AgentPlatform.Tests.Support;

internal sealed class FakeGitHubAppClient : IGitHubAppClient
{
    public List<IReadOnlyList<HookDelivery>> Pages { get; } = [];

    public int PagesRead { get; private set; }

    public HashSet<long> Refused { get; } = [];

    public List<long> Redelivered { get; } = [];

    public List<string> Repositories { get; } = [];

    public Exception? Failure { get; set; }

    public Task<HookDeliveryPage> ListHookDeliveriesAsync(string? cursor, CancellationToken cancellationToken)
    {
        if (Failure is not null)
        {
            return Task.FromException<HookDeliveryPage>(Failure);
        }

        PagesRead++;
        var index = cursor is null ? 0 : int.Parse(cursor, System.Globalization.CultureInfo.InvariantCulture);
        var deliveries = index < Pages.Count ? Pages[index] : [];
        return Task.FromResult(new HookDeliveryPage(deliveries, index + 1 < Pages.Count ? (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : null));
    }

    public Task<bool> RedeliverAsync(long deliveryId, CancellationToken cancellationToken)
    {
        Redelivered.Add(deliveryId);
        return Task.FromResult(!Refused.Contains(deliveryId));
    }

    public Task<IReadOnlyList<string>> ListInstallationRepositoriesAsync(string owner, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([.. Repositories]);
}

internal sealed class RecordingSignaler : IRenovateTriageSignaler
{
    public List<CheckSuiteCompletedSignal> Signals { get; } = [];

    public Task SignalAsync(CheckSuiteCompletedSignal signal, CancellationToken cancellationToken)
    {
        Signals.Add(signal);
        return Task.CompletedTask;
    }
}

internal sealed class FakeWorkflowProbe : ITriageWorkflowProbe
{
    public HashSet<(string Repo, int PrNumber)> Running { get; } = [];

    public Task<bool> IsRunningAsync(string repo, int prNumber, CancellationToken cancellationToken) =>
        Task.FromResult(Running.Contains((repo, prNumber)));
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
