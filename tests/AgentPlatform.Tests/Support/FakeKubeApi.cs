using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using k8s;
using k8s.Models;

namespace AgentPlatform.Tests.Support;

public sealed class PodBehavior
{
    public string Logs { get; set; } = string.Empty;

    public int ExitCode { get; set; }

    public string TerminalPhase { get; set; } = "Succeeded";

    public string? TerminalReason { get; set; }

    public string? WaitingReason { get; set; }

    public DateTime? CreatedAt { get; set; }

    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static PodBehavior Completes(string logs, int exitCode = 0, string phase = "Succeeded")
    {
        var behavior = new PodBehavior { Logs = logs, ExitCode = exitCode, TerminalPhase = phase };
        behavior.Release.TrySetResult();
        return behavior;
    }
}

public sealed partial class FakeKubeApi : DelegatingHandler
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, V1Pod> _pods = [];
    private readonly Dictionary<string, V1Secret> _secrets = [];
    private readonly Dictionary<string, PodBehavior> _behaviors = [];

    public const string SuccessLogs = """{"type":"result","subtype":"success","is_error":false,"result":"ok","stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":2}}""";

    public PodBehavior DefaultBehavior { get; set; } = PodBehavior.Completes(SuccessLogs);

    public List<string> Operations { get; } = [];

    public int PodCreateAttempts { get; private set; }

    public int PodsCreated { get; private set; }

    public Func<HttpRequestMessage, HttpResponseMessage?>? Interceptor { get; set; }

    public IReadOnlyDictionary<string, V1Pod> Pods
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, V1Pod>(_pods);
            }
        }
    }

    public IReadOnlyDictionary<string, V1Secret> Secrets
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, V1Secret>(_secrets);
            }
        }
    }

    public IKubernetes CreateClient() =>
        new Kubernetes(new KubernetesClientConfiguration { Host = "http://fake-kube.test" }, this);

    public void SetBehavior(string podName, PodBehavior behavior)
    {
        lock (_gate)
        {
            _behaviors[podName] = behavior;
        }
    }

    public void Seed(V1Pod pod)
    {
        pod.Metadata.Uid = Guid.NewGuid().ToString();
        pod.Metadata.ResourceVersion = "1";
        pod.Metadata.CreationTimestamp = DateTime.UtcNow;
        pod.Status = new V1PodStatus { Phase = "Pending" };
        lock (_gate)
        {
            _pods[pod.Metadata.Name] = pod;
        }
    }

    public void SeedSecret(V1Secret secret)
    {
        lock (_gate)
        {
            _secrets[secret.Metadata.Name] = secret;
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = Interceptor?.Invoke(request) ?? await RouteAsync(request, cancellationToken);
        response.RequestMessage = request;
        return response;
    }

    private async Task<HttpResponseMessage> RouteAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {

        var path = request.RequestUri!.AbsolutePath;
        var query = request.RequestUri.Query;

        if (PodsCollection().Match(path) is { Success: true } collection)
        {
            return request.Method == HttpMethod.Post
                ? await CreatePodAsync(request, collection.Groups[1].Value, cancellationToken)
                : Watch(query, cancellationToken);
        }

        if (PodLog().Match(path) is { Success: true } log)
        {
            return Log(log.Groups[2].Value);
        }

        if (SinglePod().Match(path) is { Success: true } single)
        {
            return request.Method == HttpMethod.Delete ? Delete(single.Groups[2].Value) : Get(single.Groups[2].Value);
        }

        if (SecretsCollection().IsMatch(path) && request.Method == HttpMethod.Post)
        {
            return await CreateSecretAsync(request, cancellationToken);
        }

        if (SingleSecret().Match(path) is { Success: true } singleSecret && request.Method == HttpMethod.Delete)
        {
            return DeleteSecret(singleSecret.Groups[2].Value);
        }

        return Json(HttpStatusCode.NotFound, Status(404, "NotFound", $"unhandled {request.Method} {path}"));
    }

    private async Task<HttpResponseMessage> CreatePodAsync(HttpRequestMessage request, string ns, CancellationToken cancellationToken)
    {
        var pod = KubernetesJson.Deserialize<V1Pod>(await request.Content!.ReadAsStringAsync(cancellationToken));
        lock (_gate)
        {
            PodCreateAttempts++;
            Operations.Add($"POST pod {pod.Metadata.Name}");
            if (_pods.ContainsKey(pod.Metadata.Name))
            {
                return Json(HttpStatusCode.Conflict, Status(409, "AlreadyExists", $"pods \"{pod.Metadata.Name}\" already exists"));
            }

            pod.Metadata.NamespaceProperty = ns;
            pod.Metadata.Uid = Guid.NewGuid().ToString();
            pod.Metadata.ResourceVersion = "1";
            pod.Metadata.CreationTimestamp = _behaviors.TryGetValue(pod.Metadata.Name, out var behavior) && behavior.CreatedAt is { } at ? at : DateTime.UtcNow;
            pod.Status = new V1PodStatus { Phase = "Pending" };
            _pods[pod.Metadata.Name] = pod;
            PodsCreated++;
            return Json(HttpStatusCode.Created, KubernetesJson.Serialize(pod));
        }
    }

    private async Task<HttpResponseMessage> CreateSecretAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var secret = KubernetesJson.Deserialize<V1Secret>(await request.Content!.ReadAsStringAsync(cancellationToken));
        lock (_gate)
        {
            Operations.Add($"POST secret {secret.Metadata.Name}");
            if (_secrets.ContainsKey(secret.Metadata.Name))
            {
                return Json(HttpStatusCode.Conflict, Status(409, "AlreadyExists", $"secrets \"{secret.Metadata.Name}\" already exists"));
            }

            _secrets[secret.Metadata.Name] = secret;
            return Json(HttpStatusCode.Created, KubernetesJson.Serialize(secret));
        }
    }

    private HttpResponseMessage DeleteSecret(string name)
    {
        lock (_gate)
        {
            Operations.Add($"DELETE secret {name}");
            if (!_secrets.Remove(name, out var secret))
            {
                return Json(HttpStatusCode.NotFound, Status(404, "NotFound", $"secrets \"{name}\" not found"));
            }

            return Json(HttpStatusCode.OK, KubernetesJson.Serialize(secret));
        }
    }

    private HttpResponseMessage Get(string name)
    {
        lock (_gate)
        {
            return _pods.TryGetValue(name, out var pod)
                ? Json(HttpStatusCode.OK, KubernetesJson.Serialize(Snapshot(pod)))
                : Json(HttpStatusCode.NotFound, Status(404, "NotFound", $"pods \"{name}\" not found"));
        }
    }

    private HttpResponseMessage Delete(string name)
    {
        lock (_gate)
        {
            Operations.Add($"DELETE pod {name}");
            if (!_pods.Remove(name, out var pod))
            {
                return Json(HttpStatusCode.NotFound, Status(404, "NotFound", $"pods \"{name}\" not found"));
            }

            return Json(HttpStatusCode.OK, KubernetesJson.Serialize(pod));
        }
    }

    private HttpResponseMessage Log(string name)
    {
        lock (_gate)
        {
            if (!_pods.ContainsKey(name))
            {
                return Json(HttpStatusCode.NotFound, Status(404, "NotFound", $"pods \"{name}\" not found"));
            }

            var logs = (_behaviors.GetValueOrDefault(name) ?? DefaultBehavior).Logs;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(logs, Encoding.UTF8, "text/plain") };
        }
    }

    private HttpResponseMessage Watch(string query, CancellationToken cancellationToken)
    {
        var selector = WatchSelector().Match(Uri.UnescapeDataString(query));
        var name = selector.Groups[1].Value;
        var pipe = new Pipe();
        _ = StreamEventsAsync(name, pipe.Writer, cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(pipe.Reader.AsStream()) };
    }

    private async Task StreamEventsAsync(string name, PipeWriter writer, CancellationToken cancellationToken)
    {
        try
        {
            PodBehavior behavior;
            lock (_gate)
            {
                behavior = _behaviors.GetValueOrDefault(name) ?? DefaultBehavior;
                if (_pods.TryGetValue(name, out var pod) && pod.Status.Phase == "Pending")
                {
                    pod.Status = new V1PodStatus { Phase = behavior.WaitingReason is null ? "Running" : "Pending", ContainerStatuses = WaitingStatus(behavior) };
                    pod.Metadata.ResourceVersion = "2";
                }
            }

            await WriteEventAsync(writer, name, cancellationToken);
            await behavior.Release.Task.WaitAsync(cancellationToken);

            lock (_gate)
            {
                if (_pods.TryGetValue(name, out var pod))
                {
                    pod.Status = new V1PodStatus
                    {
                        Phase = behavior.TerminalPhase,
                        Reason = behavior.TerminalReason,
                        ContainerStatuses =
                        [
                            new V1ContainerStatus
                            {
                                Name = "claude-code",
                                State = new V1ContainerState
                                {
                                    Terminated = new V1ContainerStateTerminated { ExitCode = behavior.ExitCode, Reason = behavior.TerminalReason },
                                },
                            },
                        ],
                    };
                    pod.Metadata.ResourceVersion = "3";
                }
            }

            await WriteEventAsync(writer, name, cancellationToken);
            await writer.CompleteAsync();
        }
        catch (OperationCanceledException)
        {
            await writer.CompleteAsync();
        }
    }

    private async Task WriteEventAsync(PipeWriter writer, string name, CancellationToken cancellationToken)
    {
        string json;
        lock (_gate)
        {
            if (!_pods.TryGetValue(name, out var pod))
            {
                return;
            }

            json = $$"""{"type":"MODIFIED","object":{{KubernetesJson.Serialize(Snapshot(pod))}}}""" + "\n";
        }

        await writer.WriteAsync(Encoding.UTF8.GetBytes(json), cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }

    private static IList<V1ContainerStatus>? WaitingStatus(PodBehavior behavior) =>
        behavior.WaitingReason is null
            ? null
            : [new V1ContainerStatus { Name = "claude-code", State = new V1ContainerState { Waiting = new V1ContainerStateWaiting { Reason = behavior.WaitingReason } } }];

    private static V1Pod Snapshot(V1Pod pod) => KubernetesJson.Deserialize<V1Pod>(KubernetesJson.Serialize(pod));

    private static string Status(int code, string reason, string message) =>
        JsonSerializer.Serialize(new
        {
            kind = "Status",
            apiVersion = "v1",
            status = "Failure",
            code,
            reason,
            message,
        });

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [GeneratedRegex(@"^/api/v1/namespaces/([^/]+)/pods$")]
    private static partial Regex PodsCollection();

    [GeneratedRegex(@"^/api/v1/namespaces/([^/]+)/pods/([^/]+)$")]
    private static partial Regex SinglePod();

    [GeneratedRegex(@"^/api/v1/namespaces/([^/]+)/pods/([^/]+)/log$")]
    private static partial Regex PodLog();

    [GeneratedRegex(@"^/api/v1/namespaces/([^/]+)/secrets$")]
    private static partial Regex SecretsCollection();

    [GeneratedRegex(@"^/api/v1/namespaces/([^/]+)/secrets/([^/]+)$")]
    private static partial Regex SingleSecret();

    [GeneratedRegex(@"metadata\.name=([^&]+)")]
    private static partial Regex WatchSelector();
}
