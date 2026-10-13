using System.Text;

namespace AgentPlatform.Host.Agents;

public sealed class BoundedLineReader(TextReader reader, int maxLineChars = BoundedLineReader.MaxLineChars)
{
    public const int MaxLineChars = 1024 * 1024;

    private const int BufferSize = 8192;

    private readonly char[] _buffer = new char[BufferSize];
    private int _position;
    private int _length;

    public int SkippedLines { get; private set; }

    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        var overflowed = false;
        var sawAny = false;
        while (true)
        {
            if (_position == _length)
            {
                _length = await reader.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                _position = 0;
                if (_length == 0)
                {
                    return EndOfInput(line, overflowed, sawAny);
                }
            }

            sawAny = true;
            var span = _buffer.AsSpan(_position, _length - _position);
            var newline = span.IndexOf('\n');
            var take = newline < 0 ? span.Length : newline;
            if (!overflowed)
            {
                if (line.Length + take > maxLineChars)
                {
                    overflowed = true;
                    line.Clear();
                }
                else
                {
                    line.Append(span[..take]);
                }
            }

            _position += newline < 0 ? take : take + 1;
            if (newline < 0)
            {
                continue;
            }

            if (overflowed)
            {
                SkippedLines++;
                overflowed = false;
                sawAny = false;
                continue;
            }

            return TrimCarriageReturn(line);
        }
    }

    private string? EndOfInput(StringBuilder line, bool overflowed, bool sawAny)
    {
        if (!sawAny)
        {
            return null;
        }

        if (overflowed)
        {
            SkippedLines++;
            return null;
        }

        return TrimCarriageReturn(line);
    }

    private static string TrimCarriageReturn(StringBuilder line)
    {
        if (line.Length > 0 && line[^1] == '\r')
        {
            line.Length--;
        }

        return line.ToString();
    }
}
