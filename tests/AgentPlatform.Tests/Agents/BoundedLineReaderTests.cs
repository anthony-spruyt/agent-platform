using AgentPlatform.Host.Agents;

namespace AgentPlatform.Tests.Agents;

public class BoundedLineReaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<List<string>> ReadAllAsync(BoundedLineReader reader)
    {
        var lines = new List<string>();
        while (await reader.ReadLineAsync(Ct) is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }

    [Fact]
    public async Task ReadLine_SplitsOnLineFeedsAndStripsCarriageReturns()
    {
        var reader = new BoundedLineReader(new StringReader("a\r\nb\n\nc"), 100);

        Assert.Equal(["a", "b", "", "c"], await ReadAllAsync(reader));
        Assert.Equal(0, reader.SkippedLines);
    }

    [Fact]
    public async Task ReadLine_SkipsAndCountsLinesOverTheCapWithoutKeepingTheirContent()
    {
        var reader = new BoundedLineReader(new StringReader("ok\n" + new string('x', 5000) + "\nnext\n" + new string('y', 101)), 100);

        Assert.Equal(["ok", "next"], await ReadAllAsync(reader));
        Assert.Equal(2, reader.SkippedLines);
    }

    [Fact]
    public async Task ReadLine_AcceptsALineOfExactlyTheCap()
    {
        var reader = new BoundedLineReader(new StringReader(new string('x', 100) + "\nz"), 100);

        Assert.Equal([new string('x', 100), "z"], await ReadAllAsync(reader));
        Assert.Equal(0, reader.SkippedLines);
    }
}
