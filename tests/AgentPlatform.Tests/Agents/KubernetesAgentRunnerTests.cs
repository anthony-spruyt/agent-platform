using System.Net;
using AgentPlatform.Host.Agents;
using AgentPlatform.Tests.Support;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentPlatform.Tests.Agents;

public class KubernetesAgentRunnerTests
{
    private const string SessionKey = "0123456789abcdef0123456789abcdef";

    private const string Success =
        """{"type":"system","subtype":"init","session_id":"s-1"}""" + "\n" +
        """{"type":"result","subtype":"success","is_error":false,"result":"all good","stop_reason":"end_turn","duration_ms":10,"num_turns":2,"total_cost_usd":0.5,"session_id":"s-1","usage":{"input_tokens":10,"output_tokens":20}}""";

    private static readonly AgentRunRequest Request =
        new("spike-1", "run-1", "claude", "Say hello", AgentTier.Read, 1800);

    private static readonly string PodName = AgentPodNaming.PodName("spike-1", "run-1", "claude");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class ExtraSecretData(IReadOnlyDictionary<string, string> data) : IRunSecretContributor
    {
        public Task<IReadOnlyDictionary<string, string>> GetAdditionalDataAsync(AgentRunRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(data);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static KubernetesAgentRunner NewRunner(
        FakeKubeApi api,
        Action<AgentPodsOptions>? configure = null,
        IRunSecretContributor[]? contributors = null,
        TimeProvider? time = null)
    {
        var options = new AgentPodsOptions();
        configure?.Invoke(options);
        return new KubernetesAgentRunner(
            api.CreateClient(),
            new AgentPodSpecBuilder(options),
            new AgentSessionTokens(SessionKey),
            contributors ?? [],
            options,
            time ?? TimeProvider.System,
            NullLogger<KubernetesAgentRunner>.Instance);
    }

    private static FakeKubeApi ApiWith(string logs, int exitCode = 0, string phase = "Succeeded", string? reason = null)
    {
        var api = new FakeKubeApi();
        var behavior = PodBehavior.Completes(logs, exitCode, phase);
        behavior.TerminalReason = reason;
        api.SetBehavior(PodName, behavior);
        return api;
    }

    [Fact]
    public async Task Run_CreatesTheDeterministicPodAndReturnsTheParsedResult()
    {
        var api = ApiWith(Success);

        var result = await NewRunner(api).RunAsync(Request, Ct);

        Assert.Equal(AgentRunOutcome.Succeeded, result.Outcome);
        Assert.Equal(PodName, result.PodName);
        Assert.Equal("all good", result.ResultText);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new AgentRunUsage(10, 20, 0.5, 10, 2, "s-1"), result.Usage);
        Assert.Equal(1, api.PodCreateAttempts);
    }

    [Fact]
    public async Task Run_CreatesThePerRunSecretAfterThePodWithAnOwnerReferenceToIt()
    {
        var api = ApiWith(Success);

        await NewRunner(api).RunAsync(Request, Ct);

        Assert.Equal([$"POST pod {PodName}", $"POST secret {PodName}"], api.Operations);
        var secret = api.Secrets[PodName];
        var owner = Assert.Single(secret.Metadata.OwnerReferences);
        Assert.Equal("Pod", owner.Kind);
        Assert.Equal("v1", owner.ApiVersion);
        Assert.Equal(PodName, owner.Name);
        Assert.False(string.IsNullOrEmpty(owner.Uid));
        Assert.Null(owner.BlockOwnerDeletion);
    }

    [Fact]
    public async Task Run_PutsAValidSessionTokenForTheRunInTheSecret()
    {
        var api = ApiWith(Success);

        await NewRunner(api).RunAsync(Request, Ct);

        var secret = api.Secrets[PodName];
        Assert.Equal("agent-platform-agents", secret.Metadata.NamespaceProperty);
        var token = secret.StringData["session-token"];
        Assert.True(new AgentSessionTokens(SessionKey).TryValidate(token, out var run));
        Assert.Equal(new AgentRunIdentity("spike-1", "run-1"), run);
    }

    [Fact]
    public async Task Run_IssuesATokenThatExpiresShortlyAfterThePodDeadline()
    {
        var api = ApiWith(Success);
        var before = DateTimeOffset.UtcNow;

        await NewRunner(api).RunAsync(Request, Ct);

        var token = api.Secrets[PodName].StringData["session-token"];
        var lifetime = TimeSpan.FromSeconds(Request.DeadlineSeconds + AgentRunTimeouts.SchedulingGraceSeconds);
        var grace = TimeSpan.FromSeconds(AgentRunTimeouts.TokenGraceSeconds);
        Assert.True(new AgentSessionTokens(SessionKey, new FixedTime(before + lifetime + (grace / 2))).TryValidate(token, out _));
        Assert.False(new AgentSessionTokens(SessionKey, new FixedTime(before + lifetime + grace + TimeSpan.FromMinutes(2))).TryValidate(token, out _));
    }

    [Fact]
    public async Task Run_WhenAnEarlierPodOfTheSameNameLeftItsSecret_ReplacesItWithOneOwnedByTheCurrentPod()
    {
        var api = ApiWith(Success);
        api.SeedSecret(new V1Secret
        {
            Metadata = new V1ObjectMeta
            {
                Name = PodName,
                NamespaceProperty = "agent-platform-agents",
                OwnerReferences = [new V1OwnerReference { ApiVersion = "v1", Kind = "Pod", Name = PodName, Uid = "uid-of-a-deleted-pod" }],
            },
            StringData = new Dictionary<string, string> { ["session-token"] = "stale" },
        });

        await NewRunner(api).RunAsync(Request, Ct);

        var secret = api.Secrets[PodName];
        Assert.Equal(api.Pods[PodName].Metadata.Uid, Assert.Single(secret.Metadata.OwnerReferences).Uid);
        Assert.NotEqual("stale", secret.StringData["session-token"]);
    }

    [Fact]
    public async Task Run_ReplacesTheSecretWithOnlyCreateAndDelete()
    {
        var api = ApiWith(Success);
        var requests = new List<string>();
        api.Interceptor = request =>
        {
            requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            return null;
        };

        await NewRunner(api).RunAsync(Request, Ct);
        await NewRunner(api).RunAsync(Request, Ct);

        Assert.DoesNotContain(requests, r => r.StartsWith("GET", StringComparison.Ordinal) && r.Contains("/secrets", StringComparison.Ordinal));
        Assert.Contains($"DELETE secret {PodName}", api.Operations);
        Assert.Single(api.Secrets);
    }

    [Fact]
    public async Task Run_StoresAdditionalSecretDataFromContributors()
    {
        var api = ApiWith(Success);
        var contributor = new ExtraSecretData(new Dictionary<string, string> { ["github-token"] = "ghs_example" });

        await NewRunner(api, contributors: [contributor]).RunAsync(Request, Ct);

        Assert.Equal("ghs_example", api.Secrets[PodName].StringData["github-token"]);
        Assert.True(api.Secrets[PodName].StringData.ContainsKey("session-token"));
    }

    [Fact]
    public async Task Run_LeavesTheEndedPodForTheWorkflowToDelete()
    {
        var api = ApiWith(Success);

        await NewRunner(api).RunAsync(Request, Ct);

        Assert.Contains(PodName, api.Pods.Keys);
        Assert.DoesNotContain($"DELETE pod {PodName}", api.Operations);
    }

    [Fact]
    public async Task Run_WhenThePodAlreadyExists_ReattachesWithoutCreatingASecondPod()
    {
        var api = ApiWith(Success);
        var held = new PodBehavior { Logs = Success };
        api.SetBehavior(PodName, held);
        using var cancelFirst = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var first = NewRunner(api).RunAsync(Request, cancelFirst.Token);
        await WaitUntilAsync(() => api.Secrets.ContainsKey(PodName));
        await cancelFirst.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Single(api.Pods);

        held.Release.SetResult();
        var second = await NewRunner(api).RunAsync(Request, Ct);

        Assert.Equal(AgentRunOutcome.Succeeded, second.Outcome);
        Assert.Equal(2, api.PodCreateAttempts);
        Assert.Equal(1, api.PodsCreated);
        Assert.Single(api.Pods);
        Assert.Single(api.Secrets);
    }

    [Fact]
    public async Task Run_WhenAWorkerDiedBeforeCreatingTheSecret_ReattachAddsIt()
    {
        var api = ApiWith(Success);
        api.Seed(new AgentPodSpecBuilder(new AgentPodsOptions()).Build(Request));

        var result = await NewRunner(api).RunAsync(Request, Ct);

        Assert.Equal(AgentRunOutcome.Succeeded, result.Outcome);
        Assert.Contains($"POST secret {PodName}", api.Operations);
        Assert.Equal(0, api.PodsCreated);
    }

    [Fact]
    public async Task Run_WhenCancelled_LeavesThePodForTheRetryToReattach()
    {
        var api = ApiWith(Success);
        api.SetBehavior(PodName, new PodBehavior { Logs = Success });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var run = NewRunner(api).RunAsync(Request, cts.Token);
        await WaitUntilAsync(() => api.Secrets.ContainsKey(PodName));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Contains(PodName, api.Pods.Keys);
        Assert.DoesNotContain($"DELETE pod {PodName}", api.Operations);
    }

    [Fact]
    public async Task Run_WhenTheApiFailsTransiently_ThrowsAndKeepsThePod()
    {
        var api = ApiWith(Success);
        api.Interceptor = request =>
            request.RequestUri!.AbsolutePath.EndsWith("/log", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") }
                : null;

        await Assert.ThrowsAnyAsync<Exception>(() => NewRunner(api).RunAsync(Request, Ct));

        Assert.Contains(PodName, api.Pods.Keys);
    }

    [Theory]
    [InlineData("")]
    [InlineData("""{"type":"system","subtype":"init"}""")]
    [InlineData("""{"type":"result","subtype":"success","is_error":false,"result":"","stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":3}}""")]
    [InlineData("""{"type":"result","subtype":"success","is_error":false,"result":"","stop_reason":null,"usage":{"input_tokens":1,"output_tokens":3}}""")]
    [InlineData("""{"type":"result","subtype":"success","is_error":false,"result":"x","stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":0}}""")]
    [InlineData("""{"type":"result","subtype":"error_max_turns","is_error":true,"result":"x","usage":{"input_tokens":1,"output_tokens":3}}""")]
    public async Task Run_WhenStdoutIsEmptyOrCutOff_ReportsTruncated(string logs)
    {
        var api = ApiWith(logs);

        var result = await NewRunner(api).RunAsync(Request, Ct);

        Assert.Equal(AgentRunOutcome.Truncated, result.Outcome);
        Assert.Contains("incomplete", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Run_WhenStopReasonIsNullButTheResultHasText_Succeeds()
    {
        var api = ApiWith("""{"type":"result","subtype":"success","is_error":false,"result":"x","stop_reason":null,"usage":{"input_tokens":1,"output_tokens":3}}""");

        var result = await NewRunner(api).RunAsync(Request, Ct);

        Assert.Equal(AgentRunOutcome.Succeeded, result.Outcome);
    }

    [Fact]
    public async Task Run_WhenTheClientTimesOutOnARead_IsRetryableNotTimedOut()
    {
        var api = ApiWith(Success);
        var timedOut = false;
        api.Interceptor = request =>
        {
            if (!timedOut && request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith($"/pods/{PodName}", StringComparison.Ordinal))
            {
                timedOut = true;
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException());
            }

            return null;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NewRunner(api).RunAsync(Request, Ct));

        Assert.Contains(PodName, api.Pods.Keys);
        Assert.DoesNotContain($"DELETE pod {PodName}", api.Operations);
    }

    [Fact]
    public async Task Run_WhenThePodDisappearsWhileWaiting_ReportsPodFailedAndDoesNotRecreateIt()
    {
        var api = ApiWith(Success);
        api.Interceptor = request =>
            request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith($"/pods/{PodName}", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"kind":"Status","apiVersion":"v1","status":"Failure","code":404,"reason":"NotFound"}""") }
                : null;

        var result = await NewRunner(api).RunAsync(Request, Ct);

        Assert.Equal(AgentRunOutcome.PodFailed, result.Outcome);
        Assert.Contains("no longer exists", result.Detail, StringComparison.Ordinal);
        Assert.Equal(1, api.PodCreateAttempts);
    }

    [Fact]
    public async Task Run_WhenTheWatchReportsThePodDeleted_ReportsPodFailedWithoutWaitingForTheWatchToEnd()
    {
        var api = ApiWith(Success);
        api.SetBehavior(PodName, new PodBehavior { Logs = Success });
        api.Interceptor = request =>
        {
            if (request.Method != HttpMethod.Get || !request.RequestUri!.AbsolutePath.EndsWith("/pods", StringComparison.Ordinal))
            {
                return null;
            }

            var pod = new V1Pod { Metadata = new V1ObjectMeta { Name = PodName }, Status = new V1PodStatus { Phase = "Running" } };
            var pipe = new System.IO.Pipelines.Pipe();
            var line = $$"""{"type":"DELETED","object":{{k8s.KubernetesJson.Serialize(pod)}}}""" + "\n";
            _ = pipe.Writer.WriteAsync(System.Text.Encoding.UTF8.GetBytes(line)).AsTask();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(pipe.Reader.AsStream()) };
        };

        var result = await NewRunner(api, o => o.SchedulingGraceSeconds = 0).RunAsync(Request with { DeadlineSeconds = 5 }, Ct);

        Assert.Equal(AgentRunOutcome.PodFailed, result.Outcome);
        Assert.Contains("no longer exists", result.Detail, StringComparison.Ordinal);
        Assert.Equal(1, api.PodCreateAttempts);
    }

    [Fact]
    public async Task Run_WhenRetriedAfterTheRunEnded_ReattachesToTheEndedPodInsteadOfStartingANewOne()
    {
        var api = ApiWith(Success);

        var first = await NewRunner(api).RunAsync(Request, Ct);
        var second = await NewRunner(api).RunAsync(Request, Ct);

        Assert.Equal(AgentRunOutcome.Succeeded, first.Outcome);
        Assert.Equal(AgentRunOutcome.Succeeded, second.Outcome);
        Assert.Equal(1, api.PodsCreated);
    }

    [Theory]
    [InlineData("""{"type":"result","subtype":"success","is_error":true,"result":"Claude AI usage limit reached|1760000000","usage":{"input_tokens":1,"output_tokens":0}}""", 1, "Failed")]
    [InlineData("""{"type":"rate_limit_event","rate_limit_info":{"status":"rejected"}}""", 1, "Failed")]
    [InlineData("""{"type":"result","subtype":"success","is_error":true,"result":"5-hour limit reached ∙ resets 3am","usage":{"input_tokens":1,"output_tokens":0}}""", 0, "Succeeded")]
    public async Task Run_WhenClaudeReportsAUsageLimit_ReportsUsageLimit(string logs, int exitCode, string phase)
    {
        var api = ApiWith(logs, exitCode, phase);

        var result = await NewRunner(api).RunAsync(Request, Ct);

        Assert.Equal(AgentRunOutcome.UsageLimit, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Detail));
    }

    [Fact]
    public async Task Run_WhenThePodFails_ReportsPodFailedWithTheExitCode()
    {
        var api = ApiWith("boom", exitCode: 137, phase: "Failed");

        var result = await NewRunner(api).RunAsync(Request, Ct);

        Assert.Equal(AgentRunOutcome.PodFailed, result.Outcome);
        Assert.Equal(137, result.ExitCode);
    }

    [Fact]
    public async Task Run_WhenTheKubernetesDeadlineKillsThePod_ReportsTimedOut()
    {
        var api = ApiWith(Success, exitCode: 137, phase: "Failed", reason: "DeadlineExceeded");

        var result = await NewRunner(api).RunAsync(Request, Ct);

        Assert.Equal(AgentRunOutcome.TimedOut, result.Outcome);
    }

    [Fact]
    public async Task Run_WhenThePodNeverFinishes_TimesOut()
    {
        var api = ApiWith(Success);
        api.SetBehavior(PodName, new PodBehavior { Logs = Success });

        var result = await NewRunner(api, o => o.SchedulingGraceSeconds = 0).RunAsync(Request with { DeadlineSeconds = 1 }, Ct);

        Assert.Equal(AgentRunOutcome.TimedOut, result.Outcome);
    }

    [Fact]
    public async Task Run_WhenTheImageCannotBePulled_FailsFastInsteadOfWaitingForTheDeadline()
    {
        var api = ApiWith(Success);
        api.SetBehavior(PodName, new PodBehavior { Logs = Success, WaitingReason = "ImagePullBackOff" });

        var result = await NewRunner(api).RunAsync(Request, Ct);

        Assert.Equal(AgentRunOutcome.PodFailed, result.Outcome);
        Assert.Contains("ImagePullBackOff", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_WhenContainerConfigIsStillBroken_AfterTheGracePeriod_FailsFast()
    {
        var api = ApiWith(Success);
        api.SetBehavior(PodName, new PodBehavior { Logs = Success, WaitingReason = "CreateContainerConfigError" });

        var later = new FixedTime(DateTimeOffset.UtcNow.AddMinutes(10));
        var result = await NewRunner(api, time: later).RunAsync(Request, Ct);

        Assert.Equal(AgentRunOutcome.PodFailed, result.Outcome);
        Assert.Contains("CreateContainerConfigError", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_WhenContainerConfigErrorIsFresh_KeepsWaitingForTheSecret()
    {
        var api = ApiWith(Success);
        var behavior = new PodBehavior { Logs = Success, WaitingReason = "CreateContainerConfigError" };
        api.SetBehavior(PodName, behavior);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NewRunner(api).RunAsync(Request, cts.Token));

        Assert.Contains(PodName, api.Pods.Keys);
    }

    [Fact]
    public async Task Cancel_DeletesThePod()
    {
        var api = ApiWith(Success);
        api.Seed(new AgentPodSpecBuilder(new AgentPodsOptions()).Build(Request));

        await NewRunner(api).CancelAsync(Request, Ct);

        Assert.Empty(api.Pods);
    }

    [Fact]
    public async Task Cancel_WhenThePodIsAlreadyGone_Succeeds()
    {
        var api = ApiWith(Success);

        await NewRunner(api).CancelAsync(Request, Ct);

        Assert.Empty(api.Pods);
    }

    [Fact]
    public async Task Run_RejectsAnOutOfRangeDeadlineBeforeTouchingTheCluster()
    {
        var api = ApiWith(Success);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => NewRunner(api).RunAsync(Request with { DeadlineSeconds = 7200 }, Ct));

        Assert.Empty(api.Operations);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition was not met in time.");
            await Task.Delay(20, Ct);
        }
    }
}
