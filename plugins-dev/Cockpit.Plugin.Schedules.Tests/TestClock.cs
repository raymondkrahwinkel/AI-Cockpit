namespace Cockpit.Plugin.Schedules.Tests;

// shortcut: Advance fires all pending timers once; add due-time ordering when a test needs partial advances.
internal sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    private event Action? Advanced;

    public override DateTimeOffset GetUtcNow() => Now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        new HeldTimer(this, callback, state);

    public void Advance(TimeSpan elapsed)
    {
        Now += elapsed;
        Advanced?.Invoke();
    }

    private sealed class HeldTimer : ITimer
    {
        private readonly TestClock _clock;
        private readonly Action _fire;

        public HeldTimer(TestClock clock, TimerCallback callback, object? state)
        {
            _clock = clock;
            _fire = () =>
            {
                Dispose();
                callback(state);
            };
            clock.Advanced += _fire;
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose() => _clock.Advanced -= _fire;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
