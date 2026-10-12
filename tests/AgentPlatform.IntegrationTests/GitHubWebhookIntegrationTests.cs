using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Host.Workflows;
using Aspire.Hosting.Testing;
using Temporalio.Client;
using Temporalio.Common;

namespace AgentPlatform.IntegrationTests;

[Collection(AppHostSharedFixture.Name)]
public class GitHubWebhookIntegrationTests(AppHostFixture fixture)
{
    private const string LocalWebhookSecret = "local-development-only-webhook-secret";

    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task ASignedCheckSuite_StartsTheTriageWorkflowThatTheWorkerRuns()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TestTimeout);
        var ct = timeout.Token;
        var pr = RandomNumberGenerator.GetInt32(1, int.MaxValue);
        var delivery = Guid.NewGuid().ToString();
        var body = JsonSerializer.Serialize(new
        {
            action = "completed",
            check_suite = new
            {
                id = 42,
                head_branch = "renovate/foo",
                head_sha = "0123456789abcdef0123456789abcdef01234567",
                conclusion = "success",
                app = new { slug = "github-actions" },
                pull_requests = new[] { new { number = pr, head = new { repo = new { id = 9 } }, @base = new { repo = new { id = 9 } } } },
            },
            repository = new { id = 9, full_name = "anthony-spruyt/widgets" },
        });
        using var web = fixture.App.CreateHttpClient("web", "webhooks");
        using var request = new HttpRequestMessage(HttpMethod.Post, GitHubWebhookEndpoint.Path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-GitHub-Event", "check_suite");
        request.Headers.TryAddWithoutValidation("X-GitHub-Delivery", delivery);
        request.Headers.TryAddWithoutValidation(WebhookSignature.HeaderName, WebhookSignature.Compute(Encoding.UTF8.GetBytes(body), LocalWebhookSecret));

        using var response = await web.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var client = await fixture.ConnectAsync(ct);
        var handle = client.GetWorkflowHandle(RenovateTriageWorkflow.WorkflowId("anthony-spruyt/widgets", pr));
        var history = await WaitForAsync(
            handle,
            h => h.Events.Any(e => e.ActivityTaskScheduledEventAttributes?.ActivityType.Name == RenovateTriageActivities.AssessName),
            ct);

        var signalEvent = Assert.Single(history.Events, e => e.WorkflowExecutionSignaledEventAttributes is not null);
        var json = signalEvent.WorkflowExecutionSignaledEventAttributes.Input.Payloads_[0].Data.ToStringUtf8();
        Assert.Contains($"\"deliveryId\":\"{delivery}\"", json, StringComparison.Ordinal);
        Assert.Contains("\"checkSuiteId\":42", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnsignedRequest_IsRejectedBeforeAnythingIsStarted()
    {
        using var web = fixture.App.CreateHttpClient("web", "webhooks");
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");

        using var response = await web.PostAsync(GitHubWebhookEndpoint.Path, content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<WorkflowHistory> WaitForAsync(WorkflowHandle handle, Func<WorkflowHistory, bool> condition, CancellationToken ct)
    {
        while (true)
        {
            var history = await handle.FetchHistoryAsync();
            if (condition(history))
            {
                return history;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }
    }
}
