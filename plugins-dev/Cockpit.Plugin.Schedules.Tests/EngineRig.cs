using Cockpit.Plugin.Schedules.Contracts;
using Cockpit.Plugin.Schedules.Engine;

namespace Cockpit.Plugin.Schedules.Tests;

// One engine's surroundings: storage that outlives an engine (a restart), a held clock, and an action that records
// when it ran and does whatever the test hands it.
internal sealed class EngineRig(DateTimeOffset start)
{
    public InMemoryPluginStorage Storage { get; } = new();

    public TestClock Clock { get; } = new(start);

    public List<(string TaskId, DateTimeOffset At)> Executed { get; } = [];

    public Func<ScheduledTask, Task<ActionResult>> Action { get; set; } = _ => Task.FromResult(new ActionResult(RunOutcome.Succeeded));

    public int MaxConcurrentRuns { get; set; } = 2;

    public IReadOnlyList<RunRecord> History => new RunStore(Storage, Clock).Load();

    public static ScheduledTask NewTask(string schedule, MissedRunPolicy missedRuns = MissedRunPolicy.RunOnceWhenBack, string id = "stand-up", string zone = "Europe/Amsterdam") => new()
    {
        Id = id,
        Name = id,
        Schedule = schedule,
        TimeZone = zone,
        MissedRuns = missedRuns,
        Action = new TaskAction(TaskActionKind.Notification, "Stand-up"),
        Delivery = [new DeliveryTarget(DeliveryTarget.CockpitNotification)],
    };

    public void Add(params ScheduledTask[] tasks)
    {
        var store = new TaskStore(Storage);
        store.Save([.. store.Load() ?? [], .. tasks]);
    }

    public void Replace(params ScheduledTask[] tasks) => new TaskStore(Storage).Save(tasks);

    public ScheduleEngine Start() => new(
        new TaskStore(Storage),
        new MarkStore(Storage),
        new RunStore(Storage, Clock),
        (task, _, _) =>
        {
            lock (Executed)
            {
                Executed.Add((task.Id, Clock.Now));
            }

            return Action(task);
        },
        () => MaxConcurrentRuns,
        Clock);

    // Ticks the way the timer does, every 30 seconds, until the clock reaches `until`.
    public async Task WalkAsync(ScheduleEngine engine, DateTimeOffset until)
    {
        while (Clock.Now < until)
        {
            Clock.Now += ScheduleEngine.Tick;
            await engine.TickAsync();
        }
    }
}
