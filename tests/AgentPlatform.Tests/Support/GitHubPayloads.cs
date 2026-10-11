using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentPlatform.Host.GitHub;

namespace AgentPlatform.Tests.Support;

internal static class GitHubPayloads
{
    public const string Secret = "webhook-secret-for-tests";
    public const string Repo = "octo/widgets";
    public const long RepoId = 4242;
    public const string HeadSha = "0123456789abcdef0123456789abcdef01234567";

    public static object PullRequest(int number, long headRepoId = RepoId) => new
    {
        number,
        head = new { @ref = "renovate/foo", sha = HeadSha, repo = new { id = headRepoId, name = "widgets" } },
        @base = new { @ref = "main", sha = "base", repo = new { id = RepoId, name = "widgets" } },
    };

    public static string CheckSuite(
        string action = "completed",
        string? branch = "renovate/foo",
        string? appSlug = "github-actions",
        string? conclusion = "success",
        object[]? pullRequests = null,
        long suiteId = 99) =>
        JsonSerializer.Serialize(new
        {
            action,
            check_suite = new
            {
                id = suiteId,
                head_branch = branch,
                head_sha = HeadSha,
                conclusion,
                app = appSlug is null ? null : new { slug = appSlug },
                pull_requests = pullRequests ?? [PullRequest(7)],
            },
            repository = new { id = RepoId, full_name = Repo },
        });

    public static string Sign(string body, string secret = Secret) =>
        WebhookSignature.Compute(Encoding.UTF8.GetBytes(body), secret);

    private static readonly Lazy<string> SharedPem = new(() =>
    {
        using var key = RSA.Create(2048);
        return key.ExportRSAPrivateKeyPem();
    });

    public static string PrivateKeyPem => SharedPem.Value;

    public static RSA NewKey() => RSA.Create(2048);

    public static void AddWorkerSettings(IDictionary<string, string?> settings)
    {
        settings.TryAdd("GitHub:AppId", "12345");
        settings.TryAdd("GitHub:PrivateKey", PrivateKeyPem);
    }
}
