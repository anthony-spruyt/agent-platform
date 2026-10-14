using System.Text;

namespace AgentPlatform.Host.GitHub;

/// <summary>
/// Keeps the last characters of a job log up to its last <c>##[error]</c> line, in bounded memory. The runner writes the
/// post-job cleanup after the failing step, so a plain tail is mostly noise. A log with no such line keeps its plain tail.
/// </summary>
internal sealed class JobLogTail(int maxChars)
{
    private const string FailureMarker = "##[error]";
    private const int MarkerWindow = 64;

    private readonly StringBuilder _throughFailure = new();
    private readonly StringBuilder _afterFailure = new();
    private readonly StringBuilder _lineHead = new();
    private bool _failureSeen;

    public void Append(ReadOnlySpan<char> chunk)
    {
        while (!chunk.IsEmpty)
        {
            var newline = chunk.IndexOf('\n');
            if (newline < 0)
            {
                AppendToLine(chunk);
                break;
            }

            AppendToLine(chunk[..(newline + 1)]);
            chunk = chunk[(newline + 1)..];
            EndLine();
        }

        Trim(_afterFailure);
    }

    public string Result()
    {
        EndLine();
        var text = _failureSeen ? _throughFailure : _afterFailure;
        return text.ToString(Math.Max(0, text.Length - maxChars), Math.Min(text.Length, maxChars));
    }

    private void AppendToLine(ReadOnlySpan<char> text)
    {
        _afterFailure.Append(text);
        var room = MarkerWindow - _lineHead.Length;
        if (room > 0)
        {
            _lineHead.Append(text[..Math.Min(room, text.Length)]);
        }
    }

    private void EndLine()
    {
        if (_lineHead.Length > 0 && _lineHead.ToString().Contains(FailureMarker, StringComparison.Ordinal))
        {
            _failureSeen = true;
            _throughFailure.Append(_afterFailure);
            _afterFailure.Clear();
            Trim(_throughFailure);
        }

        _lineHead.Clear();
    }

    private void Trim(StringBuilder text)
    {
        if (text.Length > 2 * maxChars)
        {
            text.Remove(0, text.Length - maxChars);
        }
    }
}
