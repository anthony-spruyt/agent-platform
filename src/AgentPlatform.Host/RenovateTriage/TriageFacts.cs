using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AgentPlatform.Host.GitHub;

namespace AgentPlatform.Host.RenovateTriage;

public sealed partial class TriageFacts
{
    private readonly string _repo;
    private readonly int _prNumber;
    private readonly string _headSha;
    private readonly string? _reason;
    private readonly CiSummary? _ci;
    private readonly int _failingJobs;
    private readonly int _heldBack;

    public TriageFacts(string repo, int prNumber, string headSha, string? reason = null, int failingJobs = 0, int heldBack = 0, CiSummary? ci = null)
    {
        GitHubRepoName.Parse(repo, nameof(repo));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(prNumber);
        ArgumentOutOfRangeException.ThrowIfNegative(failingJobs);
        ArgumentOutOfRangeException.ThrowIfNegative(heldBack);
        ArgumentOutOfRangeException.ThrowIfNegative(ci?.Checks ?? 0, nameof(ci));
        if (!HeadSha().IsMatch(headSha ?? string.Empty))
        {
            throw new ArgumentException("The head sha must be 7 to 64 lowercase hex characters.", nameof(headSha));
        }

        if (reason is not null && !Reason().IsMatch(reason))
        {
            throw new ArgumentException("The reason must be a short lowercase token.", nameof(reason));
        }

        _repo = repo;
        _prNumber = prNumber;
        _headSha = headSha!;
        _reason = reason;
        _ci = ci;
        _failingJobs = failingJobs;
        _heldBack = heldBack;
    }

    public string Render()
    {
        var text = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"Repository: {_repo}\nPull request: #{_prNumber}\nHead commit: {_headSha}");
        if (_reason is not null)
        {
            text.Append(CultureInfo.InvariantCulture, $"\nTriage reason: {_reason}");
        }

        if (_ci is not null)
        {
            text.Append(CultureInfo.InvariantCulture, $"\nCI: {RenderCi(_ci)}");
        }

        if (_failingJobs > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"\nFailing CI jobs with a log tail below: {_failingJobs}");
        }

        if (_heldBack > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"\nTexts left out because the scanner held them back: {_heldBack}");
        }

        return text.ToString();
    }

    private static string RenderCi(CiSummary ci)
    {
        var count = ci.Checks == 1 ? "1 check" : string.Create(CultureInfo.InvariantCulture, $"{ci.Checks} checks");
        return (ci.State, ci.Checks) switch
        {
            (CiState.Passed, _) => "green",
            (CiState.Pending, 0) => "pending",
            (CiState.Pending, _) => $"pending ({count})",
            _ => $"failing ({count})",
        };
    }

    [GeneratedRegex("^[0-9a-f]{7,64}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HeadSha();

    [GeneratedRegex("^[a-z][a-z0-9-]{0,39}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Reason();
}
