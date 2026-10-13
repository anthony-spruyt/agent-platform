using AgentPlatform.Host.PullRequests;

namespace AgentPlatform.Tests.Support;

internal sealed class FakeTextScanner : IUntrustedTextScanner
{
    private readonly List<(string Fragment, ScanOutcome Outcome)> _rules = [];

    public ScanOutcome Default { get; set; } = ScanOutcome.Clean;

    public (string Fragment, Exception Failure)? FailOn { get; set; }

    public List<string> Scanned { get; } = [];

    public void Return(string fragment, ScanOutcome outcome) => _rules.Add((fragment, outcome));

    public Task<TextScan> ScanAsync(string text, CancellationToken cancellationToken)
    {
        Scanned.Add(text);
        if (FailOn is { } fail && text.Contains(fail.Fragment, StringComparison.Ordinal))
        {
            return Task.FromException<TextScan>(fail.Failure);
        }

        var rule = _rules.Find(r => text.Contains(r.Fragment, StringComparison.Ordinal));
        return Task.FromResult(new TextScan(rule.Fragment is null ? Default : rule.Outcome, ScannedText.Create(text)));
    }
}
