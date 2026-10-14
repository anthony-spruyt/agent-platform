using AgentPlatform.Host.Agents;
using AgentPlatform.Host.Workflows;
using Temporalio.Exceptions;

namespace AgentPlatform.Tests.Workflows;

public class AgentPodStepTests
{
    private static SubmitResultPayload Payload(string step = "claude", int attempt = 1) =>
        new("success", "ok", null, step, attempt);

    private static AgentPodStep Step(int attempt = 1, Func<SubmitResultPayload, string?>? parse = null) =>
        new("claude", attempt, parse ?? (_ => null));

    private static string ErrorType(Action act) =>
        Assert.IsType<ApplicationFailureException>(Record.Exception(act)).ErrorType!;

    [Fact]
    public void Validate_AcceptsAResultForTheCurrentStepAndAttempt()
    {
        AgentPodStep.Validate(Step(), Payload());
    }

    [Theory]
    [InlineData("other", 1)]
    [InlineData("claude", 2)]
    public void Validate_RejectsAResultForAnotherStepOrAttempt(string step, int attempt)
    {
        Assert.Equal(AgentResultUpdates.StaleRunErrorType, ErrorType(() => AgentPodStep.Validate(Step(), Payload(step, attempt))));
    }

    [Fact]
    public void Validate_RejectsAnAttemptOneTokenAgainstAttemptTwo()
    {
        Assert.Equal(AgentResultUpdates.StaleRunErrorType, ErrorType(() => AgentPodStep.Validate(Step(attempt: 2), Payload(attempt: 1))));
    }

    [Fact]
    public void Validate_RejectsAResultWhileNoStepIsRunning()
    {
        Assert.Equal(AgentResultUpdates.StaleRunErrorType, ErrorType(() => AgentPodStep.Validate(null, Payload())));
    }

    [Fact]
    public async Task Validate_RejectsASecondResultAfterOneWasAccepted()
    {
        var step = Step();
        await step.AcceptAsync(Payload());

        Assert.Equal(AgentResultUpdates.ResultAlreadySubmittedErrorType, ErrorType(() => AgentPodStep.Validate(step, Payload())));
    }

    [Fact]
    public async Task AnInvalidResult_IsRejectedWithTheParserErrorAndMayBeResubmittedOnce()
    {
        var step = Step(parse: _ => "bad shape");

        var first = await Assert.ThrowsAsync<ApplicationFailureException>(() => step.AcceptAsync(Payload()));
        AgentPodStep.Validate(step, Payload());
        await Assert.ThrowsAsync<ApplicationFailureException>(() => step.AcceptAsync(Payload()));

        Assert.Equal(AgentResultUpdates.InvalidResultErrorType, first.ErrorType);
        Assert.Equal("bad shape", first.Message);
        Assert.Equal(AgentResultUpdates.InvalidResultErrorType, ErrorType(() => AgentPodStep.Validate(step, Payload())));
    }

    [Fact]
    public async Task AValidResultAfterAnInvalidOne_IsAccepted()
    {
        var calls = 0;
        var step = Step(parse: _ => ++calls == 1 ? "bad" : null);

        await Assert.ThrowsAsync<ApplicationFailureException>(() => step.AcceptAsync(Payload()));
        await step.AcceptAsync(Payload());

        Assert.Equal(AgentResultUpdates.ResultAlreadySubmittedErrorType, ErrorType(() => AgentPodStep.Validate(step, Payload())));
    }
}
