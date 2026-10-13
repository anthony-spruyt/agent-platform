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
    private int _lineStart;
    private bool _failureSeen;

    public void Append(ReadOnlySpan<char> chunk)
    {
        while (!chunk.IsEmpty)
        {
            var newline = chunk.IndexOf('\n');
            if (newline < 0)
            {
                _afterFailure.Append(chunk);
                break;
            }

            _afterFailure.Append(chunk[..(newline + 1)]);
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

    private void EndLine()
    {
        var length = Math.Min(_afterFailure.Length - _lineStart, MarkerWindow);
        if (length > 0 && _afterFailure.ToString(_lineStart, length).Contains(FailureMarker, StringComparison.Ordinal))
        {
            _failureSeen = true;
            _throughFailure.Append(_afterFailure);
            _afterFailure.Clear();
            Trim(_throughFailure);
        }

        _lineStart = _afterFailure.Length;
    }

    private void Trim(StringBuilder text)
    {
        if (text.Length <= 2 * maxChars)
        {
            return;
        }

        var drop = text.Length - maxChars;
        text.Remove(0, drop);
        if (ReferenceEquals(text, _afterFailure))
        {
            _lineStart = Math.Max(0, _lineStart - drop);
        }
    }
}
