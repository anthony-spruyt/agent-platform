using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AgentPlatform.Host.PullRequests;

public sealed class UntrustedTextScannerClient(IHttpClientFactory httpClients, IOptions<UntrustedTextScannerOptions> options, TimeProvider time) : IUntrustedTextScanner, IDisposable
{
    public const string ClientName = "text-scanner";

    // Each scanner copy has one model thread, so texts go through one at a time
    private readonly SemaphoreSlim _serial = new(1, 1);

    public void Dispose() => _serial.Dispose();

    public async Task<TextScan> ScanAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        var item = ScannedText.Create(text);

        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return new TextScan(await ScanWithRetryAsync(item, cancellationToken).ConfigureAwait(false), item);
        }
        finally
        {
            _serial.Release();
        }
    }

    private async Task<ScanOutcome> ScanWithRetryAsync(ScannedText item, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var started = time.GetTimestamp();
        while (true)
        {
            var attempt = await AttemptAsync(item, settings, cancellationToken).ConfigureAwait(false);
            if (attempt is not null)
            {
                return attempt.Value;
            }

            if (time.GetElapsedTime(started) + settings.RetryDelay >= settings.RetryBudget)
            {
                return ScanOutcome.Unchecked;
            }

            await Task.Delay(settings.RetryDelay, time, cancellationToken).ConfigureAwait(false);
        }
    }

    // Null means the scanner was busy or unreachable, which the caller retries
    private async Task<ScanOutcome?> AttemptAsync(ScannedText item, UntrustedTextScannerOptions settings, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(settings.RequestTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var http = httpClients.CreateClient(ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(settings.BaseUrl, UriKind.Absolute), "v1/scan"))
        {
            Content = JsonContent.Create(new ScanRequest([new ScanItem(item.Hash, item.Text)], [])),
        };

        try
        {
            using var response = await http.SendAsync(request, linked.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                return ScanOutcome.Unchecked;
            }

            var body = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            return Parse(body, item.Hash);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ScanOutcome.Unchecked;
        }
    }

    private static ScanOutcome Parse(string body, string hash)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("flagged", out var flagged)
                || flagged.ValueKind != JsonValueKind.Array)
            {
                return ScanOutcome.Unchecked;
            }

            var flaggedHashes = new List<string>();
            foreach (var entry in flagged.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String)
                {
                    return ScanOutcome.Unchecked;
                }

                flaggedHashes.Add(entry.GetString()!);
            }

            return flaggedHashes.Contains(hash, StringComparer.Ordinal) ? ScanOutcome.Flagged : ScanOutcome.Clean;
        }
        catch (JsonException)
        {
            return ScanOutcome.Unchecked;
        }
    }

    private sealed record ScanItem(string Hash, string Text);

    private sealed record ScanRequest(IReadOnlyList<ScanItem> New, IReadOnlyList<string> Known);
}
