using AgentPlatform.Host.PullRequests;

namespace AgentPlatform.Tests.Support;

internal sealed class FakeCheckRunReporter : ICheckRunReporter
{
    public List<CheckRunReport> Reports { get; } = [];

    public Exception? Failure { get; set; }

    public Task ReportAsync(CheckRunReport report, CancellationToken cancellationToken)
    {
        if (Failure is not null)
        {
            return Task.FromException(Failure);
        }

        Reports.Add(report);
        return Task.CompletedTask;
    }
}
