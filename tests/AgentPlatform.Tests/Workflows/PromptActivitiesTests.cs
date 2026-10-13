using System.Runtime.Versioning;
using AgentPlatform.Host.Agents;
using AgentPlatform.Host.Workflows;
using Microsoft.Extensions.Logging;
using Temporalio.Exceptions;

namespace AgentPlatform.Tests.Workflows;

public sealed class PromptActivitiesTests : IDisposable
{
    private const string Name = AgentPrompts.RenovateTriage;

    private readonly string _directory = Directory.CreateTempSubdirectory("prompts-").FullName;

    private sealed class CapturingLogger : ILogger<PromptActivities>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static PromptActivities Create(string? directory, CapturingLogger? logger = null) =>
        new(new AgentPromptsOptions { Directory = directory }, logger ?? new CapturingLogger());

    private void WriteOverride(string content) => File.WriteAllText(Path.Combine(_directory, $"{Name}.md"), content);

    [Fact]
    public async Task AnOverrideFile_ReplacesTheEmbeddedPrompt()
    {
        WriteOverride("custom {{repo}}");

        var body = await Create(_directory).LoadPromptAsync(Name);

        Assert.Equal("custom {{repo}}", body);
    }

    [Fact]
    public async Task AMissingOverrideFile_FallsBackToTheEmbeddedPromptAndWarns()
    {
        var logger = new CapturingLogger();

        var body = await Create(_directory, logger).LoadPromptAsync(Name);

        Assert.Equal(AgentPrompts.Load(Name), body);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(Name, warning.Message, StringComparison.Ordinal);
        Assert.Contains("embedded", warning.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task AnUnsetDirectory_UsesTheEmbeddedPrompt(string? directory)
    {
        var body = await Create(directory).LoadPromptAsync(Name);

        Assert.Equal(AgentPrompts.Load(Name), body);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \n\t ")]
    public async Task AnEmptyOrBlankOverrideFile_FailsWithoutFallingBack(string content)
    {
        WriteOverride(content);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => Create(_directory).LoadPromptAsync(Name));

        Assert.True(failure.NonRetryable);
    }

    [Fact]
    public async Task AnOverrideFileOver64KiB_FailsWithoutFallingBack()
    {
        WriteOverride(new string('a', PromptActivities.MaxOverrideBytes + 1));

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => Create(_directory).LoadPromptAsync(Name));

        Assert.True(failure.NonRetryable);
    }

    [Fact]
    public async Task AnOverrideFileOfExactly64KiB_IsAccepted()
    {
        var content = new string('a', PromptActivities.MaxOverrideBytes);
        WriteOverride(content);

        Assert.Equal(content, await Create(_directory).LoadPromptAsync(Name));
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task AnUnreadableOverrideFile_FailsWithoutFallingBack()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && Environment.UserName != "root", "File permissions are not enforced here.");
        WriteOverride("custom");
        File.SetUnixFileMode(Path.Combine(_directory, $"{Name}.md"), UnixFileMode.None);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => Create(_directory).LoadPromptAsync(Name));

        Assert.True(failure.NonRetryable);
    }

    [Theory]
    [InlineData("no-such-prompt")]
    [InlineData("../renovate-triage")]
    public async Task AnUnknownPromptName_Fails(string name)
    {
        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => Create(_directory).LoadPromptAsync(name));

        Assert.True(failure.NonRetryable);
    }
}
