using AgentPlatform.Host.Livemap;

namespace AgentPlatform.Tests.Livemap;

// Posts one notice per build, as the real client does: a second ask for the same build finds the first and posts nothing.
internal sealed class FakeLivemapNotices : ILivemapNotices
{
    public List<(long BuildId, string AppLogin, DateTimeOffset Since, string Body)> Posts { get; } = [];

    public int Asked { get; private set; }

    public Exception? Failure { get; set; }

    public Task<bool> PostOnceAsync(long buildId, string appLogin, DateTimeOffset since, string body, CancellationToken cancellationToken)
    {
        Asked++;
        if (Failure is not null)
        {
            return Task.FromException<bool>(Failure);
        }

        if (Posts.Any(post => post.BuildId == buildId))
        {
            return Task.FromResult(false);
        }

        Posts.Add((buildId, appLogin, since, body));
        return Task.FromResult(true);
    }
}
