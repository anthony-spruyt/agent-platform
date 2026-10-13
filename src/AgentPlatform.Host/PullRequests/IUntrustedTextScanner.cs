namespace AgentPlatform.Host.PullRequests;

public enum ScanOutcome
{
    Clean,
    Flagged,
    Unchecked,
}

public sealed record TextScan(ScanOutcome Outcome, ScannedText Text);

public interface IUntrustedTextScanner
{
    Task<TextScan> ScanAsync(string text, CancellationToken cancellationToken);
}
