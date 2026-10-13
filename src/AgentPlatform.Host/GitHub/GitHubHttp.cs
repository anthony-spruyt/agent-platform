using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
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

    public static async Task<T?> SendOrNullAsync<T>(HttpClient client, HttpRequestMessage request, string operation, CancellationToken cancellationToken)
        where T : class
    {
        using (request)
        {
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

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

    public static async Task<string?> ReadTextOrNullAsync(HttpClient client, HttpRequestMessage request, string operation, int maxChars, bool keepTail, CancellationToken cancellationToken)
    {
        using (request)
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                return null;
            }

            EnsureSuccess(response, operation);
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var reader = new StreamReader(stream);
                return await ReadBoundedAsync(reader, maxChars, keepTail, cancellationToken).ConfigureAwait(false);
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

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maxChars, bool keepTail, CancellationToken cancellationToken)
    {
        var head = new StringBuilder();
        var tail = keepTail ? new JobLogTail(maxChars) : null;
        var buffer = new char[8192];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (tail is not null)
            {
                tail.Append(buffer.AsSpan(0, read));
                continue;
            }

            head.Append(buffer, 0, read);
            if (head.Length >= maxChars)
            {
                break;
            }
        }

        return tail is not null ? tail.Result() : head.ToString(0, Math.Min(head.Length, maxChars));
    }
}
