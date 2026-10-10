using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace AgentPlatform.Host.Agents;

public sealed partial class KubernetesAgentRunner(
    IKubernetes kubernetes,
    AgentPodSpecBuilder podSpecs,
    AgentSessionTokens sessionTokens,
    IEnumerable<IRunSecretContributor> secretContributors,
    AgentPodsOptions options,
    TimeProvider time,
    ILogger<KubernetesAgentRunner> logger) : IAgentRunner
{
    private const int WatchTimeoutSeconds = 300;
    private static readonly TimeSpan _configErrorGrace = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan _watchRetryDelay = TimeSpan.FromSeconds(2);

    public async Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken cancellationToken)
    {
        var spec = podSpecs.Build(request);
        var name = spec.Metadata.Name;

        var pod = await CreateOrAttachAsync(spec, cancellationToken).ConfigureAwait(false);
        await EnsureSecretAsync(pod, request, cancellationToken).ConfigureAwait(false);

        using var waitCap = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        waitCap.CancelAfter(TimeSpan.FromSeconds(request.DeadlineSeconds + options.SchedulingGraceSeconds));

        AgentRunResult result;
        try
        {
            var (finalPod, stuckReason) = await WaitForEndAsync(name, waitCap.Token).ConfigureAwait(false);
            result = stuckReason is null
                ? await InterpretAsync(finalPod, cancellationToken).ConfigureAwait(false)
                : new AgentRunResult(AgentRunOutcome.PodFailed, name, null, null, $"Pod stuck: {stuckReason}", null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            result = new AgentRunResult(AgentRunOutcome.TimedOut, name, null, null, "Pod did not finish within the run deadline plus scheduling grace.", null);
        }

        await DeletePodAsync(name, cancellationToken).ConfigureAwait(false);
        LogRunEnded(logger, name, result.Outcome);
        return result;
    }

    public Task CancelAsync(AgentRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return DeletePodAsync(AgentPodNaming.PodName(request.WorkflowId, request.RunId, request.Step), cancellationToken);
    }

    private async Task<V1Pod> CreateOrAttachAsync(V1Pod spec, CancellationToken cancellationToken)
    {
        var name = spec.Metadata.Name;
        try
        {
            var created = await kubernetes.CoreV1.CreateNamespacedPodAsync(spec, options.Namespace, cancellationToken: cancellationToken).ConfigureAwait(false);
            LogPodCreated(logger, name);
            return created;
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.Conflict)
        {
            LogPodReattached(logger, name);
            return await GetPodAsync(name, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EnsureSecretAsync(V1Pod pod, AgentRunRequest request, CancellationToken cancellationToken)
    {
        var data = new Dictionary<string, string>
        {
            [AgentPodSpecBuilder.SessionTokenKey] = sessionTokens.Issue(request.WorkflowId, request.RunId),
        };
        foreach (var contributor in secretContributors)
        {
            foreach (var (key, value) in await contributor.GetAdditionalDataAsync(request, cancellationToken).ConfigureAwait(false))
            {
                data[key] = value;
            }
        }

        var secret = new V1Secret
        {
            Metadata = new V1ObjectMeta
            {
                Name = pod.Metadata.Name,
                NamespaceProperty = options.Namespace,
                Labels = new Dictionary<string, string> { [AgentPodSpecBuilder.ManagedByLabel] = AgentPodSpecBuilder.ManagedByValue },
                OwnerReferences =
                [
                    new V1OwnerReference { ApiVersion = "v1", Kind = "Pod", Name = pod.Metadata.Name, Uid = pod.Metadata.Uid },
                ],
            },
            Type = "Opaque",
            StringData = data,
        };

        try
        {
            await kubernetes.CoreV1.CreateNamespacedSecretAsync(secret, options.Namespace, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.Conflict)
        {
            LogSecretExists(logger, pod.Metadata.Name);
        }
    }

    private Task<V1Pod> GetPodAsync(string name, CancellationToken cancellationToken) =>
        kubernetes.CoreV1.ReadNamespacedPodAsync(name, options.Namespace, cancellationToken: cancellationToken);

    private async Task<(V1Pod Pod, string? StuckReason)> WaitForEndAsync(string name, CancellationToken cancellationToken)
    {
        var pod = await GetPodAsync(name, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            if (HasEnded(pod, out var stuck))
            {
                return (pod, stuck);
            }

            try
            {
                var watch = kubernetes.CoreV1.WatchListNamespacedPodAsync(
                    options.Namespace,
                    fieldSelector: $"metadata.name={name}",
                    resourceVersion: pod.Metadata.ResourceVersion,
                    timeoutSeconds: WatchTimeoutSeconds,
                    cancellationToken: cancellationToken);

                await foreach (var (_, updated) in watch.ConfigureAwait(false))
                {
                    pod = updated;
                    if (HasEnded(pod, out stuck))
                    {
                        return (pod, stuck);
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or KubernetesException && !cancellationToken.IsCancellationRequested)
            {
                LogWatchRetry(logger, name, ex);
                await Task.Delay(_watchRetryDelay, cancellationToken).ConfigureAwait(false);
            }

            pod = await GetPodAsync(name, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool HasEnded(V1Pod pod, out string? stuckReason)
    {
        stuckReason = null;
        if (pod.Status?.Phase is "Succeeded" or "Failed")
        {
            return true;
        }

        var waiting = pod.Status?.ContainerStatuses?.Select(s => s.State?.Waiting?.Reason).FirstOrDefault(r => r is not null);
        var age = time.GetUtcNow() - new DateTimeOffset(pod.Metadata.CreationTimestamp ?? time.GetUtcNow().UtcDateTime);
        if (waiting is "ImagePullBackOff" or "InvalidImageName"
            || (waiting is "CreateContainerConfigError" && age > _configErrorGrace))
        {
            stuckReason = waiting;
            return true;
        }

        return false;
    }

    private async Task<AgentRunResult> InterpretAsync(V1Pod pod, CancellationToken cancellationToken)
    {
        var name = pod.Metadata.Name;
        var exitCode = pod.Status?.ContainerStatuses?.FirstOrDefault()?.State?.Terminated?.ExitCode;
        var summary = await ReadSummaryAsync(name, cancellationToken).ConfigureAwait(false);

        if (summary.UsageLimitReached)
        {
            return Result(AgentRunOutcome.UsageLimit, summary.UsageLimitMessage);
        }

        if (pod.Status?.Reason == "DeadlineExceeded")
        {
            return Result(AgentRunOutcome.TimedOut, "The pod hit its activeDeadlineSeconds.");
        }

        if (pod.Status?.Phase == "Failed" || exitCode is not (null or 0))
        {
            return Result(AgentRunOutcome.PodFailed, $"The agent container exited with code {exitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}.");
        }

        return summary.IsTruncated
            ? Result(AgentRunOutcome.Truncated, DescribeTruncation(summary))
            : Result(AgentRunOutcome.Succeeded, null);

        AgentRunResult Result(AgentRunOutcome outcome, string? detail) =>
            new(outcome, name, exitCode, summary.ResultText, detail, summary.Usage);
    }

    private async Task<ClaudeOutputSummary> ReadSummaryAsync(string name, CancellationToken cancellationToken)
    {
        var stream = await kubernetes.CoreV1.ReadNamespacedPodLogAsync(
            name,
            options.Namespace,
            container: AgentPodSpecBuilder.AgentContainerName,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(stream);
            return await ClaudeOutputAnalyzer.AnalyzeAsync(reader, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string DescribeTruncation(ClaudeOutputSummary summary) =>
        string.Join(
            ", ",
            "Agent response incomplete",
            $"result_event={summary.HasResultEvent}",
            $"result_subtype={summary.Subtype}",
            $"output_tokens={summary.OutputTokens}",
            $"stop_reason_null={summary.StopReasonIsNull}",
            $"result_empty={summary.ResultText is ""}");

    private async Task DeletePodAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            await kubernetes.CoreV1.DeleteNamespacedPodAsync(name, options.Namespace, gracePeriodSeconds: 0, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
        {
            LogPodAlreadyGone(logger, name);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Created agent pod {PodName}.")]
    private static partial void LogPodCreated(ILogger logger, string podName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent pod {PodName} already exists; re-attaching.")]
    private static partial void LogPodReattached(ILogger logger, string podName);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Per-run Secret {SecretName} already exists.")]
    private static partial void LogSecretExists(ILogger logger, string secretName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Watch on agent pod {PodName} failed; retrying.")]
    private static partial void LogWatchRetry(ILogger logger, string podName, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent pod {PodName} ended with {Outcome}.")]
    private static partial void LogRunEnded(ILogger logger, string podName, AgentRunOutcome outcome);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Agent pod {PodName} was already gone.")]
    private static partial void LogPodAlreadyGone(ILogger logger, string podName);
}
