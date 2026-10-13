using System.Text;
using AgentPlatform.Host.Agents;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace AgentPlatform.Host.Workflows;

public sealed partial class PromptActivities(AgentPromptsOptions options, ILogger<PromptActivities> logger)
{
    public const string LoadPromptName = "LoadPrompt";
    public const int MaxOverrideBytes = 64 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    [Activity(LoadPromptName)]
    public async Task<string> LoadPromptAsync(string name)
    {
        if (name is null || !AgentPrompts.Exists(name))
        {
            throw new ApplicationFailureException($"Unknown prompt {name}.", nonRetryable: true);
        }

        if (string.IsNullOrEmpty(options.Directory))
        {
            LogEmbedded(logger, name);
            return AgentPrompts.Load(name);
        }

        var path = Path.Combine(options.Directory, $"{name}.md");
        var body = await ReadOverrideAsync(name, path).ConfigureAwait(false);
        if (body is null)
        {
            LogMissingOverride(logger, name, path);
            return AgentPrompts.Load(name);
        }

        LogOverride(logger, name, path);
        return body;
    }

    private static async Task<string?> ReadOverrideAsync(string name, string path)
    {
        var buffer = new byte[MaxOverrideBytes + 1];
        int length;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            length = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ApplicationFailureException($"The prompt override {path} for {name} cannot be read: {ex.Message}", ex, nonRetryable: true);
        }

        if (length > MaxOverrideBytes)
        {
            throw new ApplicationFailureException($"The prompt override {path} for {name} is over {MaxOverrideBytes} bytes.", nonRetryable: true);
        }

        string body;
        try
        {
            body = StrictUtf8.GetString(buffer, 0, length);
        }
        catch (DecoderFallbackException ex)
        {
            throw new ApplicationFailureException($"The prompt override {path} for {name} is not valid UTF-8.", ex, nonRetryable: true);
        }

        if (body.StartsWith('\uFEFF'))
        {
            body = body[1..];
        }

        return string.IsNullOrWhiteSpace(body)
            ? throw new ApplicationFailureException($"The prompt override {path} for {name} is empty.", nonRetryable: true)
            : body;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Prompt {Name} loaded from the embedded default; no prompt directory is configured.")]
    private static partial void LogEmbedded(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Prompt {Name} loaded from the embedded default; no override file at {Path}.")]
    private static partial void LogMissingOverride(ILogger logger, string name, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Prompt {Name} loaded from the override file {Path}.")]
    private static partial void LogOverride(ILogger logger, string name, string path);
}
