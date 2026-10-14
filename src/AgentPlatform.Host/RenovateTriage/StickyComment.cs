using System.Text;
using System.Text.RegularExpressions;

namespace AgentPlatform.Host.RenovateTriage;

// Agent text must never act on its own (ping, link, render) or read as the platform's words.
public static partial class StickyComment
{
    public const int MaxAgentChars = 8000;

    private const string HeaderPrefix = "**Agent verdict: ";
    private const string ZeroWidthSpace = "\u200B";

    public static string Verdict(TriageVerdict verdict, string agentText, string headSha, string? model, string? runId)
    {
        ArgumentNullException.ThrowIfNull(agentText);
        ArgumentException.ThrowIfNullOrWhiteSpace(headSha);

        var body = new StringBuilder();
        body.Append(HeaderPrefix).Append(Label(verdict)).Append("**\n");

        if (!string.IsNullOrWhiteSpace(agentText))
        {
            body.Append("\n**Agent output (unverified)**\n").Append(Quote(Escape(Cap(agentText, out var truncated))));
            if (truncated)
            {
                body.Append("\n\n_Agent output truncated._");
            }

            body.Append('\n');
        }

        body.Append("\nhead ").Append(TriageRules.ShortSha(headSha));
        if (!string.IsNullOrWhiteSpace(model))
        {
            body.Append(" · model ").Append(Line(model));
        }

        if (!string.IsNullOrWhiteSpace(runId))
        {
            body.Append(" · run ").Append(Line(runId));
        }

        return body.ToString();
    }

    public static string WithVerdict(string body, TriageVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(body);
        var end = body.IndexOf('\n', StringComparison.Ordinal);
        var header = end < 0 ? body : body[..end];
        return header.StartsWith(HeaderPrefix, StringComparison.Ordinal) && header.EndsWith("**", StringComparison.Ordinal)
            ? $"{HeaderPrefix}{Label(verdict)}**{body[header.Length..]}"
            : body;
    }

    public static string Queued(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        return $"**Queued for agent review:** {Line(reason)}";
    }

    private static string Label(TriageVerdict verdict) => verdict switch
    {
        TriageVerdict.Safe or TriageVerdict.Approve => "SAFE",
        TriageVerdict.Fixable => "FIXABLE",
        TriageVerdict.Blocked => "BLOCKED",
        _ => "NEEDS REVIEW",
    };

    private static string Cap(string text, out bool truncated)
    {
        truncated = text.Length > MaxAgentChars;
        if (!truncated)
        {
            return text;
        }

        var end = char.IsHighSurrogate(text[MaxAgentChars - 1]) ? MaxAgentChars - 1 : MaxAgentChars;
        return text[..end];
    }

    private static string Quote(string text) =>
        string.Join('\n', text.Split('\n').Select(line => line.Length == 0 ? ">" : $"> {line}"));

    private static string Line(string text) => Escape(string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));

    private static string Escape(string text)
    {
        var inert = ActiveText().Replace(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'), m => m.Value + ZeroWidthSpace);
        return inert
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal);
    }

    // Marks where GitHub starts a mention, reference or autolink; the zero-width space after the match breaks it.
    [GeneratedRegex(@"[@#]|(?<![A-Za-z0-9])GH(?=-\d)|(?<![A-Za-z0-9])www(?=\.)|https?(?=://)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ActiveText();
}
