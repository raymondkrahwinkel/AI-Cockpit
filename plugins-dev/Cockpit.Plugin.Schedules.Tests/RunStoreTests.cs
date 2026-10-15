using Cockpit.Plugin.Schedules.Contracts;
using Cockpit.Plugin.Schedules.Engine;

namespace Cockpit.Plugin.Schedules.Tests;

// The history keeps 30 days and at most 256 KB of output per run.
public class RunStoreTests
{
    [Fact]
    public void ARunOlderThanThirtyDays_IsDropped_AndOutputIsCapped()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero));
        var runs = new RunStore(new InMemoryPluginStorage(), clock);
        runs.Add(_Run("old", clock.Now));
        clock.Now += RunStore.Keep + TimeSpan.FromSeconds(1);

        runs.Add(_Run("new", clock.Now) with { Output = new string('x', RunStore.MaxOutputChars + 10) });

        var kept = Assert.Single(runs.Load());
        Assert.Equal(("new", RunStore.MaxOutputChars), (kept.Id, kept.Output?.Length));
    }

    private static RunRecord _Run(string id, DateTimeOffset at) => new()
    {
        Id = id,
        TaskId = "stand-up",
        Trigger = RunTrigger.Schedule,
        Outcome = RunOutcome.Succeeded,
        StartedAt = at,
    };
}
