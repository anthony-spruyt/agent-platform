namespace AgentPlatform.Tests.Livemap;

// Stands in for the wall clock under the time-skipping server, where activities would otherwise see no time pass.
internal sealed class TickingTimeProvider(DateTimeOffset start, TimeSpan step) : TimeProvider
{
    private long _ticks;

    public override DateTimeOffset GetUtcNow() => start + (step * Interlocked.Increment(ref _ticks));
}
