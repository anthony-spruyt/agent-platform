namespace AgentPlatform.Tests.Livemap;

// A fixed clock whose timers fire at once, so a wait of seconds costs nothing and its length can be checked.
internal sealed class InstantTimeProvider(DateTimeOffset now) : TimeProvider
{
    private readonly List<TimeSpan> _waits = [];

    public IReadOnlyList<TimeSpan> Waits => _waits;

    public override DateTimeOffset GetUtcNow() => now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        _waits.Add(dueTime);
        _ = Task.Run(() => callback(state));
        return new FiredTimer();
    }

    private sealed class FiredTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => false;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
