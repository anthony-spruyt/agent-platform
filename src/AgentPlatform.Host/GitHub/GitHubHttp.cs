using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AgentPlatform.Host.GitHub;

public sealed class GitHubApiException : Exception
{
    public GitHubApiException(string message, HttpStatusCode? statusCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode? StatusCode { get; }
}

internal static class GitHubHttp
{
    public const string ClientName = "github";

    public static void Configure(HttpClient client)
    {
        client.BaseAddress = new Uri("https://api.github.com/");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("agent-platform", "1"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public static HttpRequestMessage Request(HttpMethod method, string path, string bearer, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return request;
    }

    public static async Task SendAsync(HttpClient client, HttpRequestMessage request, string operation, CancellationToken cancellationToken)
    {
        using (request)
        {
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response, operation);
        }
    }

    public static async Task<T> SendAsync<T>(HttpClient client, HttpRequestMessage request, string operation, CancellationToken cancellationToken)
        where T : class
    {
        using (request)
        {
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response, operation);

            try
            {
                return await response.Content.ReadFromJsonAsync<T>(GitHubJson.Options, cancellationToken).ConfigureAwait(false)
                    ?? throw new GitHubApiException($"GitHub {operation} returned an empty body.", response.StatusCode);
            }
            catch (JsonException ex)
            {
                throw new GitHubApiException($"GitHub {operation} returned an unreadable body.", response.StatusCode, ex);
            }
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response, string operation)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new GitHubApiException($"GitHub {operation} failed with status {(int)response.StatusCode}.", response.StatusCode);
        }
    }
}
