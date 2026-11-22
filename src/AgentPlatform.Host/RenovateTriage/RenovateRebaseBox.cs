using System.Text.RegularExpressions;

namespace AgentPlatform.Host.RenovateTriage;

public enum RebaseBoxState
{
    Missing,
    Ticked,
    AlreadyTicked,
}

public sealed record RebaseBoxTick(RebaseBoxState State, string Body);

public static partial class RenovateRebaseBox
{
    public static RebaseBoxTick Tick(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var match = RebaseCheckbox().Match(body);
        if (!match.Success)
        {
            return new RebaseBoxTick(RebaseBoxState.Missing, body);
        }

        var mark = match.Groups["mark"];
        return mark.Value == "x"
            ? new RebaseBoxTick(RebaseBoxState.AlreadyTicked, body)
            : new RebaseBoxTick(RebaseBoxState.Ticked, string.Concat(body.AsSpan(0, mark.Index), "x", body.AsSpan(mark.Index + 1)));
    }

    // Renovate's own pattern (lib/modules/platform/pr-body.ts): it reads the first match and counts only a lowercase x as ticked.
    [GeneratedRegex(@"- \[(?<mark>[\sx])\] <!-- rebase-check -->", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RebaseCheckbox();
}
