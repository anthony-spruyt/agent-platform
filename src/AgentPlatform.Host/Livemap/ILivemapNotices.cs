namespace AgentPlatform.Host.Livemap;

public interface ILivemapNotices
{
    Task<bool> PostOnceAsync(long buildId, string appLogin, DateTimeOffset since, string body, CancellationToken cancellationToken);
}
