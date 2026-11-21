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

        var match = BoxLine().Match(body);
        if (!match.Success)
        {
            return new RebaseBoxTick(RebaseBoxState.Missing, body);
        }

        var mark = match.Groups["mark"];
        return mark.Value == " "
            ? new RebaseBoxTick(RebaseBoxState.Ticked, string.Concat(body.AsSpan(0, mark.Index), "x", body.AsSpan(mark.Index + 1)))
            : new RebaseBoxTick(RebaseBoxState.AlreadyTicked, body);
    }

    [GeneratedRegex(@"^[ \t]*[-*+][ \t]+\[(?<mark>[ xX])\][ \t]*<!-- rebase-check -->", RegexOptions.Multiline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BoxLine();
}
