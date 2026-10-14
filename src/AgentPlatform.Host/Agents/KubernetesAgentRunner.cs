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
    private const int SummaryLogLimitBytes = 64 * 1024 * 1024;
    private static readonly TimeSpan _configErrorGrace = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan _watchRetryDelay = TimeSpan.FromSeconds(2);

    public async Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = AgentPodNaming.PodName(request.WorkflowId, request.RunId, request.Step);

        var pod = await StartAsync(request, name, cancellationToken).ConfigureAwait(false);
        var podDeadline = pod.Spec?.ActiveDeadlineSeconds ?? request.DeadlineSeconds;

        using var waitCap = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        waitCap.CancelAfter(TimeSpan.FromSeconds(podDeadline + options.SchedulingGraceSeconds));

        AgentRunResult result;
        try
        {
            var (finalPod, failure) = await WaitForEndAsync(name, waitCap.Token).ConfigureAwait(false);
            result = failure is null
                ? await InterpretAsync(finalPod!, cancellationToken).ConfigureAwait(false)
                : new AgentRunResult(AgentRunOutcome.PodFailed, name, null, null, failure, null);
        }
        catch (OperationCanceledException) when (waitCap.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            result = new AgentRunResult(AgentRunOutcome.TimedOut, name, null, null, "Pod did not finish within the run deadline plus scheduling grace.", null);
        }

        LogRunEnded(logger, name, result.Outcome);
        return result with { Model = options.Model };
    }

    public Task CancelAsync(AgentRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return DeletePodAsync(AgentPodNaming.PodName(request.WorkflowId, request.RunId, request.Step), cancellationToken);
    }

    private async Task<V1Pod> StartAsync(AgentRunRequest request, string name, CancellationToken cancellationToken)
    {
        var existing = await TryGetPodAsync(name, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            LogPodReattached(logger, name);
            if (!HasMountedSecret(existing))
            {
                await EnsureSecretAsync(existing, request, null, replaceExisting: false, cancellationToken).ConfigureAwait(false);
            }

            return existing;
        }

        var contributions = await ContributeAsync(request, cancellationToken).ConfigureAwait(false);
        var spec = podSpecs.Build(request with { DeadlineSeconds = EffectiveDeadline(request, contributions) });
        var created = await TryCreatePodAsync(spec, cancellationToken).ConfigureAwait(false);
        if (created is not null)
        {
            LogPodCreated(logger, name);
            await EnsureSecretAsync(created, request, contributions, replaceExisting: true, cancellationToken).ConfigureAwait(false);
            return created;
        }

        LogPodReattached(logger, name);
        var raced = await GetPodAsync(name, cancellationToken).ConfigureAwait(false);
        await EnsureSecretAsync(raced, request, contributions, replaceExisting: false, cancellationToken).ConfigureAwait(false);
        return raced;
    }

    // The kubelet mounts the Secret before any container starts, so a pod past Pending already has it; the Role cannot read Secrets to check.
    private static bool HasMountedSecret(V1Pod pod) =>
        pod.Status?.Phase is "Running" or "Succeeded" or "Failed"
        || (pod.Status?.InitContainerStatuses ?? []).Any(s => s.State?.Running is not null || s.State?.Terminated is not null);

    private async Task<V1Pod?> TryCreatePodAsync(V1Pod spec, CancellationToken cancellationToken)
    {
        try
        {
            return await kubernetes.CoreV1.CreateNamespacedPodAsync(spec, options.Namespace, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.Conflict)
        {
            return null;
        }
    }

    private async Task<List<RunSecretContribution>> ContributeAsync(AgentRunRequest request, CancellationToken cancellationToken)
    {
        var contributions = new List<RunSecretContribution>();
        foreach (var contributor in secretContributors)
        {
            contributions.Add(await contributor.ContributeAsync(request, cancellationToken).ConfigureAwait(false));
        }

        if (request.Repo is not null && !contributions.Any(c => c.Data.ContainsKey(AgentPodSpecBuilder.GitHubTokenKey)))
        {
            throw new AgentConfigurationException("GitHub:ReadApp must be configured to run an agent against a repository.");
        }

        return contributions;
    }

    private int EffectiveDeadline(AgentRunRequest request, List<RunSecretContribution> contributions)
    {
        var deadline = request.DeadlineSeconds;
        var now = time.GetUtcNow();
        foreach (var expiresAt in contributions.Select(c => c.ExpiresAt).OfType<DateTimeOffset>())
        {
            var remaining = (expiresAt - now).TotalSeconds - AgentRunTimeouts.TokenGraceSeconds - options.SchedulingGraceSeconds;
            deadline = (int)Math.Min(deadline, Math.Floor(remaining));
        }

        return deadline >= 1
            ? deadline
            : throw new InvalidOperationException("The run's GitHub token expires too soon to cover a pod deadline.");
    }

    private async Task EnsureSecretAsync(
        V1Pod pod,
        AgentRunRequest request,
        List<RunSecretContribution>? contributions,
        bool replaceExisting,
        CancellationToken cancellationToken)
    {
        var name = pod.Metadata.Name;
        contributions ??= await ContributeAsync(request, cancellationToken).ConfigureAwait(false);
        var data = new Dictionary<string, string>(podSpecs.BuildStaticSecretData(request))
        {
            [AgentPodSpecBuilder.SessionTokenKey] = sessionTokens.Issue(request.WorkflowId, request.RunId, request.Step, request.Attempt, TokenExpiry(pod, request)),
        };
        foreach (var (key, value) in contributions.SelectMany(c => c.Data))
        {
            data[key] = value;
        }

        var secret = new V1Secret
        {
            Metadata = new V1ObjectMeta
            {
                Name = name,
                NamespaceProperty = options.Namespace,
                Labels = new Dictionary<string, string> { [AgentPodSpecBuilder.ManagedByLabel] = AgentPodSpecBuilder.ManagedByValue },
                OwnerReferences =
                [
                    new V1OwnerReference { ApiVersion = "v1", Kind = "Pod", Name = name, Uid = pod.Metadata.Uid },
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
            if (!replaceExisting)
            {
                LogSecretKept(logger, name);
                return;
            }

            LogSecretReplaced(logger, name);
            await DeleteSecretAsync(name, cancellationToken).ConfigureAwait(false);
            await kubernetes.CoreV1.CreateNamespacedSecretAsync(secret, options.Namespace, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DeleteSecretAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            await kubernetes.CoreV1.DeleteNamespacedSecretAsync(name, options.Namespace, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
        {
            LogSecretAlreadyGone(logger, name);
        }
    }

    private DateTimeOffset TokenExpiry(V1Pod pod, AgentRunRequest request)
    {
        var created = new DateTimeOffset(pod.Metadata.CreationTimestamp ?? time.GetUtcNow().UtcDateTime);
        var deadline = pod.Spec?.ActiveDeadlineSeconds ?? request.DeadlineSeconds;
        return created.AddSeconds(deadline + options.SchedulingGraceSeconds + AgentRunTimeouts.TokenGraceSeconds);
    }

    private Task<V1Pod> GetPodAsync(string name, CancellationToken cancellationToken) =>
        kubernetes.CoreV1.ReadNamespacedPodAsync(name, options.Namespace, cancellationToken: cancellationToken);

    private async Task<V1Pod?> TryGetPodAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            return await GetPodAsync(name, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task<(V1Pod? Pod, string? Failure)> WaitForEndAsync(string name, CancellationToken cancellationToken)
    {
        const string PodGone = "The agent pod no longer exists.";
        var pod = await TryGetPodAsync(name, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            if (pod is null)
            {
                return (null, PodGone);
            }

            if (HasEnded(pod, out var stuck))
            {
                return (pod, stuck is null ? null : $"Pod stuck: {stuck}");
            }

            try
            {
                var watch = kubernetes.CoreV1.WatchListNamespacedPodAsync(
                    options.Namespace,
                    fieldSelector: $"metadata.name={name}",
                    resourceVersion: pod.Metadata.ResourceVersion,
                    timeoutSeconds: WatchTimeoutSeconds,
                    cancellationToken: cancellationToken);

                await foreach (var (eventType, updated) in watch.ConfigureAwait(false))
                {
                    if (eventType == WatchEventType.Deleted)
                    {
                        return (null, PodGone);
                    }

                    pod = updated;
                    if (HasEnded(pod, out stuck))
                    {
                        return (pod, stuck is null ? null : $"Pod stuck: {stuck}");
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or KubernetesException && !cancellationToken.IsCancellationRequested)
            {
                LogWatchRetry(logger, name, ex);
                await Task.Delay(_watchRetryDelay, cancellationToken).ConfigureAwait(false);
            }

            pod = await TryGetPodAsync(name, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool HasEnded(V1Pod pod, out string? stuckReason)
    {
        stuckReason = null;
        if (pod.Status?.Phase is "Succeeded" or "Failed")
        {
            return true;
        }

        stuckReason = InitContainerFailure(pod);
        if (stuckReason is not null)
        {
            return true;
        }

        var waiting = pod.Status?.ContainerStatuses?.Select(s => s.State?.Waiting?.Reason).FirstOrDefault(r => r is not null);
        if (waiting is "ImagePullBackOff" or "InvalidImageName"
            || (waiting is "CreateContainerConfigError" && PastConfigErrorGrace(pod)))
        {
            stuckReason = waiting;
            return true;
        }

        return false;
    }

    private bool PastConfigErrorGrace(V1Pod pod) =>
        time.GetUtcNow() - new DateTimeOffset(pod.Metadata.CreationTimestamp ?? time.GetUtcNow().UtcDateTime) > _configErrorGrace;

    private string? InitContainerFailure(V1Pod pod)
    {
        foreach (var status in pod.Status?.InitContainerStatuses ?? [])
        {
            if (status.State?.Terminated is { ExitCode: not 0 } terminated)
            {
                return $"init container {status.Name} exited with code {terminated.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            }

            if (status.State?.Waiting?.Reason is { } reason
                && (reason is "ImagePullBackOff" or "InvalidImageName"
                    || (reason == "CreateContainerConfigError" && PastConfigErrorGrace(pod))))
            {
                return $"init container {status.Name} is waiting with {reason}";
            }
        }

        return null;
    }

    private async Task<AgentRunResult> InterpretAsync(V1Pod pod, CancellationToken cancellationToken)
    {
        var name = pod.Metadata.Name;
        var exitCode = pod.Status?.ContainerStatuses?.FirstOrDefault()?.State?.Terminated?.ExitCode;

        if (pod.Status?.Reason == "DeadlineExceeded")
        {
            return new AgentRunResult(AgentRunOutcome.TimedOut, name, exitCode, null, "The pod hit its activeDeadlineSeconds.", null);
        }

        if (InitContainerFailure(pod) is { } initFailure)
        {
            return new AgentRunResult(AgentRunOutcome.PodFailed, name, exitCode, null, $"Pod stuck: {initFailure}", null);
        }

        var summary = await ReadSummaryAsync(name, cancellationToken).ConfigureAwait(false);

        if (summary.UsageLimitReached)
        {
            return Result(AgentRunOutcome.UsageLimit, summary.UsageLimitMessage) with { ResetsAt = summary.UsageLimitResetsAt };
        }

        if (summary.LimitStop is not null)
        {
            return Result(AgentRunOutcome.LimitExceeded, summary.LimitStop);
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
            limitBytes: SummaryLogLimitBytes,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(stream);
            var summary = await ClaudeOutputAnalyzer.AnalyzeAsync(reader, cancellationToken).ConfigureAwait(false);
            LogSkippedLines(logger, name, summary.SkippedLines);
            return summary;
        }
    }

    private static void LogSkippedLines(ILogger logger, string podName, int skippedLines)
    {
        if (skippedLines > 0)
        {
            LogSkippedLinesCore(logger, skippedLines, podName);
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

    [LoggerMessage(Level = LogLevel.Debug, Message = "Per-run Secret {SecretName} already exists; replacing it so the current pod owns it.")]
    private static partial void LogSecretReplaced(ILogger logger, string secretName);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Per-run Secret {SecretName} already exists; keeping it.")]
    private static partial void LogSecretKept(ILogger logger, string secretName);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Per-run Secret {SecretName} was already gone.")]
    private static partial void LogSecretAlreadyGone(ILogger logger, string secretName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Watch on agent pod {PodName} failed; retrying.")]
    private static partial void LogWatchRetry(ILogger logger, string podName, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped {SkippedLines} oversized log lines from agent pod {PodName}.")]
    private static partial void LogSkippedLinesCore(ILogger logger, int skippedLines, string podName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent pod {PodName} ended with {Outcome}.")]
    private static partial void LogRunEnded(ILogger logger, string podName, AgentRunOutcome outcome);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Agent pod {PodName} was already gone.")]
    private static partial void LogPodAlreadyGone(ILogger logger, string podName);
}
