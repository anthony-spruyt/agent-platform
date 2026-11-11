using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AgentPlatform.Host.Agents;

public sealed record AgentRepoContext(string Repo, int? PullRequest, string HeadSha);

// An OCI image the node mounts read-only into the agent container; the reference is pulled with the node's own registry login, never a pull secret.
public sealed partial record AgentImageVolume(string Name, string Reference)
{
    public const int MaxReferenceLength = 300;

    internal void Validate()
    {
        if (!NamePattern().IsMatch(Name ?? string.Empty))
        {
            throw new AgentConfigurationException("An image volume name must be 1 to 20 lower-case letters, digits or dashes, starting and ending with a letter or digit.");
        }

        if (Reference is not { Length: <= MaxReferenceLength } || !ReferencePattern().IsMatch(Reference))
        {
            throw new AgentConfigurationException("An image volume reference must be a registry host and path with a tag or a sha256 digest.");
        }
    }

    [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]{0,18}[a-z0-9])?\z")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[a-z0-9]+([.-][a-z0-9]+)*\.[a-z]{2,}(:[0-9]{1,5})?/[a-z0-9]+([._/-][a-z0-9]+)*(:[A-Za-z0-9_][A-Za-z0-9._-]{0,127}|@sha256:[0-9a-f]{64})\z")]
    private static partial Regex ReferencePattern();
}

public sealed record AgentRunOverrides(
    string? Model = null,
    int? MaxTurns = null,
    decimal? MaxBudgetUsd = null,
    IReadOnlyDictionary<string, string>? TokenPermissions = null,
    IReadOnlyList<AgentImageVolume>? ImageVolumes = null)
{
    public void Validate()
    {
        if (Model is not null && string.IsNullOrWhiteSpace(Model))
        {
            throw new AgentConfigurationException("The model override must not be blank.");
        }

        if (MaxTurns is < 1)
        {
            throw new AgentConfigurationException("The max turns override must be at least 1.");
        }

        if (MaxBudgetUsd is <= 0)
        {
            throw new AgentConfigurationException("The max budget override must be positive.");
        }

        if (TokenPermissions is not null)
        {
            ReadTokenPermissions.EnsureNarrowsDefault(TokenPermissions);
        }

        if (ImageVolumes is not null)
        {
            ValidateImageVolumes(ImageVolumes);
        }
    }

    private static void ValidateImageVolumes(IReadOnlyList<AgentImageVolume> volumes)
    {
        if (volumes.Count == 0)
        {
            throw new AgentConfigurationException("The image volumes override must name at least one image.");
        }

        foreach (var volume in volumes)
        {
            (volume ?? throw new AgentConfigurationException("An image volume must not be null.")).Validate();
        }

        if (volumes.Select(v => v.Name).Distinct(StringComparer.Ordinal).Count() != volumes.Count)
        {
            throw new AgentConfigurationException("Image volume names must be unique.");
        }
    }
}

public sealed record AgentRunRequest(
    string WorkflowId,
    string RunId,
    string Step,
    string Prompt,
    AgentTier Tier,
    int DeadlineSeconds,
    int Attempt = 1,
    AgentRepoContext? Repo = null,
    AgentRunOverrides? Overrides = null);

[JsonConverter(typeof(JsonStringEnumConverter<AgentRunOutcome>))]
public enum AgentRunOutcome
{
    Succeeded,
    Truncated,
    UsageLimit,
    PodFailed,
    TimedOut,
    LimitExceeded,
}

public sealed record AgentRunUsage(
    long? InputTokens,
    long? OutputTokens,
    double? CostUsd,
    long? DurationMs,
    int? NumTurns,
    string? SessionId)
{
    private const double MaxStorableCostUsd = 9_999_999_999d;

    public static decimal? ToCostUsd(double? cost) =>
        cost is { } value && double.IsFinite(value) && Math.Abs(value) <= MaxStorableCostUsd ? (decimal)value : null;
}

public sealed record AgentRunResult(
    AgentRunOutcome Outcome,
    string PodName,
    int? ExitCode,
    string? ResultText,
    string? Detail,
    AgentRunUsage? Usage,
    string? Model = null,
    DateTimeOffset? ResetsAt = null);

public sealed record AgentRunIdentity(string WorkflowId, string RunId, string Step, int Attempt);
