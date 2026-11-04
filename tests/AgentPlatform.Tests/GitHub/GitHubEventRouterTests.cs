using System.Text;
using AgentPlatform.Host.GitHub;
using AgentPlatform.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentPlatform.Tests.GitHub;

public class GitHubEventRouterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static GitHubEventRouter Router(params IGitHubEventHandler[] handlers) =>
        new(handlers, NullLogger<GitHubWebhookLog>.Instance);

    private static GitHubWebhookEvent Event(string name, string deliveryId = "d-1", string body = "{}") =>
        new(name, deliveryId, Encoding.UTF8.GetBytes(body));

    [Fact]
    public async Task AnEvent_ReachesEveryHandlerThatAcceptsIt_WithItsNameDeliveryAndBody()
    {
        var first = new RecordingEventHandler("a", "b");
        var second = new RecordingEventHandler("b");
        var other = new RecordingEventHandler("c");

        await Router(first, second, other).RouteAsync(Event("b", "d-7", """{"k":1}"""), Ct);

        foreach (var handler in new[] { first, second })
        {
            var received = Assert.Single(handler.Received);
            Assert.Equal("b", received.Name);
            Assert.Equal("d-7", received.DeliveryId);
            Assert.Equal("""{"k":1}""", Encoding.UTF8.GetString(received.Body.Span));
        }

        Assert.Empty(other.Received);
    }

    [Fact]
    public async Task AnEventNoHandlerAccepts_IsIgnored_AndReachesNoHandler()
    {
        var handler = new RecordingEventHandler("a");

        var outcome = await Router(handler).RouteAsync(Event("push"), Ct);

        Assert.Equal(GitHubEventOutcome.Ignored, outcome);
        Assert.Empty(handler.Received);
    }

    [Theory]
    [InlineData("")]
    [InlineData("A")]
    public async Task EventNamesAreMatchedExactly(string name)
    {
        var handler = new RecordingEventHandler("a");

        var outcome = await Router(handler).RouteAsync(Event(name), Ct);

        Assert.Equal(GitHubEventOutcome.Ignored, outcome);
        Assert.Empty(handler.Received);
    }

    [Fact]
    public async Task WithNoHandlersRegistered_EveryEventIsIgnored()
    {
        Assert.Equal(GitHubEventOutcome.Ignored, await Router().RouteAsync(Event("a"), Ct));
    }

    [Fact]
    public async Task AHandlerThatThrows_DoesNotStopTheOthers_AndItsFailureStillSurfaces()
    {
        var failure = new InvalidOperationException("boom");
        var failing = new RecordingEventHandler("a") { Failure = failure };
        var after = new RecordingEventHandler("a");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => Router(failing, after).RouteAsync(Event("a"), Ct));

        Assert.Same(failure, thrown);
        Assert.Single(after.Received);
    }

    [Fact]
    public async Task SeveralFailingHandlers_AreReportedTogether_AfterEveryHandlerRan()
    {
        var first = new InvalidOperationException("first");
        var second = new FormatException("second");
        var last = new RecordingEventHandler("a");

        var thrown = await Assert.ThrowsAsync<AggregateException>(() => Router(
            new RecordingEventHandler("a") { Failure = first },
            new RecordingEventHandler("a") { Failure = second },
            last).RouteAsync(Event("a"), Ct));

        Assert.Equal([first, second], thrown.InnerExceptions);
        Assert.Single(last.Received);
    }

    [Fact]
    public async Task ACancelledRequest_StopsTheFanOut_InsteadOfCountingAsAHandlerFailure()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var cancelled = new RecordingEventHandler("a") { Failure = new OperationCanceledException(cts.Token) };
        var after = new RecordingEventHandler("a");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Router(cancelled, after).RouteAsync(Event("a"), cts.Token));

        Assert.Empty(after.Received);
    }

    [Theory]
    [InlineData(GitHubEventOutcome.Ignored, GitHubEventOutcome.Ignored, GitHubEventOutcome.Ignored)]
    [InlineData(GitHubEventOutcome.Ignored, GitHubEventOutcome.Accepted, GitHubEventOutcome.Accepted)]
    [InlineData(GitHubEventOutcome.Accepted, GitHubEventOutcome.Ignored, GitHubEventOutcome.Accepted)]
    [InlineData(GitHubEventOutcome.Accepted, GitHubEventOutcome.Malformed, GitHubEventOutcome.Malformed)]
    [InlineData(GitHubEventOutcome.Malformed, GitHubEventOutcome.Accepted, GitHubEventOutcome.Malformed)]
    [InlineData(GitHubEventOutcome.Ignored, GitHubEventOutcome.Malformed, GitHubEventOutcome.Malformed)]
    public async Task TheOutcome_IsTheStrongestOneAnyHandlerReported(GitHubEventOutcome first, GitHubEventOutcome second, GitHubEventOutcome expected)
    {
        var outcome = await Router(
            new RecordingEventHandler("a") { Outcome = first },
            new RecordingEventHandler("a") { Outcome = second }).RouteAsync(Event("a"), Ct);

        Assert.Equal(expected, outcome);
    }

    [Fact]
    public void TheAcceptedEvents_AreTheUnionOfWhatTheHandlersAccept()
    {
        var router = Router(new RecordingEventHandler("a", "b"), new RecordingEventHandler("b", "c"));

        Assert.True(router.AcceptedEvents.SetEquals(["a", "b", "c"]));
    }

    [Fact]
    public void WithNoHandlers_NoEventIsAccepted()
    {
        Assert.Empty(Router().AcceptedEvents);
    }
}
