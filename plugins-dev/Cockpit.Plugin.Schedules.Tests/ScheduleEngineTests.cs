using Cockpit.Plugin.Schedules.Contracts;
using Cockpit.Plugin.Schedules.Engine;

namespace Cockpit.Plugin.Schedules.Tests;

// AC-1542: the engine on a held clock. Every scenario starts with a tick that arms the task, as a real start does.
public class ScheduleEngineTests
{
    private static DateTimeOffset Utc(int month, int day, int hour, int minute, int second = 0) =>
        new(2026, month, day, hour, minute, second, TimeSpan.Zero);

    [Fact]
    public async Task ASlotOnTime_RunsOnce_AsAScheduleRun()
    {
        var rig = new EngineRig(Utc(10, 14, 6, 59, 50));
        rig.Add(EngineRig.NewTask("0 9 * * *"));
        using var engine = rig.Start();

        await engine.TickAsync();
        await rig.WalkAsync(engine, Utc(10, 14, 7, 30));

        Assert.Equal([("stand-up", Utc(10, 14, 7, 0, 20))], rig.Executed);
        var run = Assert.Single(rig.History);
        Assert.Equal((RunTrigger.Schedule, RunOutcome.Succeeded, Utc(10, 14, 7, 0)), (run.Trigger, run.Outcome, run.ScheduledFor));
    }

    [Fact]
    public async Task ATaskArmedAfterItsSlot_DoesNotOweThatSlot()
    {
        var rig = new EngineRig(Utc(10, 14, 7, 5));
        rig.Add(EngineRig.NewTask("0 9 * * *"));
        using var engine = rig.Start();

        await engine.TickAsync();
        await rig.WalkAsync(engine, Utc(10, 14, 8, 0));

        Assert.Empty(rig.Executed);
        Assert.Empty(rig.History);
    }

    [Fact]
    public async Task TheWintertimeNight_FiresHalfPastTwoOnce()
    {
        var rig = new EngineRig(Utc(10, 24, 22, 0));
        rig.Add(EngineRig.NewTask("30 2 * * *"));
        using var engine = rig.Start();

        await engine.TickAsync();
        await rig.WalkAsync(engine, Utc(10, 25, 3, 0));

        Assert.Equal([("stand-up", Utc(10, 25, 0, 30))], rig.Executed);
    }

    [Fact]
    public async Task AClockThatJumpsBack_DoesNotReplayASlotThatFired()
    {
        var rig = new EngineRig(Utc(10, 14, 6, 59, 30));
        rig.Add(EngineRig.NewTask("0 9 * * *"));
        using var engine = rig.Start();
        await engine.TickAsync();
        await rig.WalkAsync(engine, Utc(10, 14, 7, 1));

        rig.Clock.Now = Utc(10, 14, 6, 1);
        await rig.WalkAsync(engine, Utc(10, 14, 7, 30));

        Assert.Single(rig.Executed);
    }

    [Fact]
    public async Task TenHoursAsleep_RunOnceWhenBack_GivesOneCatchUpAndNineMissed()
    {
        var rig = new EngineRig(Utc(10, 14, 0, 30));
        rig.Add(EngineRig.NewTask("0 * * * *"));
        using var engine = rig.Start();
        await engine.TickAsync();

        rig.Clock.Now = Utc(10, 14, 10, 30);
        await engine.TickAsync();

        Assert.Single(rig.Executed);
        var catchUp = Assert.Single(rig.History, run => run.Trigger == RunTrigger.CatchUp);
        Assert.Equal((RunOutcome.Succeeded, Utc(10, 14, 10, 0)), (catchUp.Outcome, catchUp.ScheduledFor));
        Assert.Equal(9, rig.History.Count(run => run.Outcome == RunOutcome.Missed));
    }

    [Fact]
    public async Task TenHoursAsleep_Skip_GivesTenMissedAndNoRun()
    {
        var rig = new EngineRig(Utc(10, 14, 0, 30));
        rig.Add(EngineRig.NewTask("0 * * * *", MissedRunPolicy.Skip));
        using var engine = rig.Start();
        await engine.TickAsync();

        rig.Clock.Now = Utc(10, 14, 10, 30);
        await engine.TickAsync();

        Assert.Empty(rig.Executed);
        Assert.Equal(10, rig.History.Count(run => run.Outcome == RunOutcome.Missed));
        Assert.Equal(10, rig.History.Count);
    }

    [Fact]
    public async Task AWeekAsleepWithAFiveMinuteTask_LogsTheNewestSlotsAndCountsTheRest()
    {
        var rig = new EngineRig(Utc(10, 14, 0, 0, 30));
        rig.Add(EngineRig.NewTask("every 5 m", MissedRunPolicy.Skip));
        using var engine = rig.Start();
        await engine.TickAsync();

        rig.Clock.Now = Utc(10, 21, 0, 2, 30);
        await engine.TickAsync();

        Assert.Equal(51, rig.History.Count);
        Assert.Single(rig.History, run => run.Reason == "1966 earlier slots missed, from 2026-10-14 00:05:00Z");
    }

    [Fact]
    public async Task ARestartBetweenMarkAndDone_LeavesTheRunInterrupted_AndDoesNotFireItAgain()
    {
        var rig = new EngineRig(Utc(10, 14, 6, 59, 50));
        rig.Add(EngineRig.NewTask("0 9 * * *"));
        rig.Action = _ => new TaskCompletionSource<ActionResult>().Task;
        var before = rig.Start();
        await before.TickAsync();
        rig.Clock.Now = Utc(10, 14, 7, 0, 20);
        _ = before.TickAsync();
        before.Dispose();
        rig.Action = _ => Task.FromResult(new ActionResult(RunOutcome.Succeeded));

        using var after = rig.Start();
        await after.TickAsync();
        await rig.WalkAsync(after, Utc(10, 14, 7, 30));

        Assert.Single(rig.Executed);
        var run = Assert.Single(rig.History);
        Assert.Equal((RunOutcome.Failed, RunStore.Interrupted), (run.Outcome, run.Reason));
    }

    [Fact]
    public async Task ASlotWhileTheTaskStillRuns_IsSkippedAsStillRunning()
    {
        var rig = new EngineRig(Utc(10, 14, 0, 0, 10));
        rig.Add(EngineRig.NewTask("every 1 m"));
        var release = new TaskCompletionSource<ActionResult>();
        rig.Action = _ => release.Task;
        using var engine = rig.Start();
        await engine.TickAsync();

        rig.Clock.Now = Utc(10, 14, 0, 1, 10);
        var first = engine.TickAsync();
        rig.Clock.Now = Utc(10, 14, 0, 2, 10);
        await engine.TickAsync();
        release.SetResult(new ActionResult(RunOutcome.Succeeded));
        await first;

        Assert.Single(rig.Executed);
        var skipped = Assert.Single(rig.History, run => run.Outcome == RunOutcome.Skipped);
        Assert.Equal((ScheduleEngine.StillRunning, Utc(10, 14, 0, 2)), (skipped.Reason, skipped.ScheduledFor));
    }

    [Fact]
    public async Task AtMostTwoRunsAtOnce_TheThirdWaitsForAFreeSlot()
    {
        var rig = new EngineRig(Utc(10, 14, 0, 0, 10));
        rig.Add(EngineRig.NewTask("every 1 m", id: "a"), EngineRig.NewTask("every 1 m", id: "b"), EngineRig.NewTask("every 1 m", id: "c"));
        var running = new List<TaskCompletionSource<ActionResult>>();
        var thirdStarted = new TaskCompletionSource();
        rig.Action = _ =>
        {
            var run = new TaskCompletionSource<ActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (running)
            {
                running.Add(run);
                if (running.Count == 3)
                {
                    thirdStarted.SetResult();
                }
            }

            return run.Task;
        };
        using var engine = rig.Start();
        await engine.TickAsync();

        rig.Clock.Now = Utc(10, 14, 0, 1, 10);
        var tick = engine.TickAsync();
        int startedTogether;
        lock (rig.Executed)
        {
            startedTogether = rig.Executed.Count;
        }

        running[0].SetResult(new ActionResult(RunOutcome.Succeeded));
        await thirdStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        lock (running)
        {
            running.ForEach(run => run.TrySetResult(new ActionResult(RunOutcome.Succeeded)));
        }

        await tick;

        Assert.Equal((2, 3), (startedTogether, rig.Executed.Count));
    }

    [Fact]
    public async Task DisabledAndUnreadableTasks_DoNotFire_AndAnUnreadableOneTurnsHealthRed()
    {
        var rig = new EngineRig(Utc(10, 14, 0, 30));
        rig.Add(
            EngineRig.NewTask("0 * * * *", id: "off") with { IsEnabled = false },
            EngineRig.NewTask("0 * * * *", id: "no-zone", zone: ""),
            EngineRig.NewTask("61 * * * *", id: "bad-cron"));
        using var engine = rig.Start();

        await engine.TickAsync();
        await rig.WalkAsync(engine, Utc(10, 14, 1, 30));

        Assert.Empty(rig.Executed);
        var health = engine.Health.Read();
        Assert.False(health.Healthy);
        Assert.Contains(health.Rows, row => row.Label == "2 enabled task(s) with an unreadable schedule or time zone");
    }

    [Fact]
    public async Task ATaskSwitchedBackOnOrRescheduled_DoesNotOweTheTimeInBetween()
    {
        var rig = new EngineRig(Utc(10, 14, 6, 59, 30));
        var task = EngineRig.NewTask("0 9 * * *");
        rig.Add(task);
        using var engine = rig.Start();
        await engine.TickAsync();
        await rig.WalkAsync(engine, Utc(10, 14, 7, 1));

        rig.Replace(task with { IsEnabled = false });
        await rig.WalkAsync(engine, Utc(10, 14, 7, 2));
        rig.Clock.Now = Utc(10, 20, 8, 0);
        rig.Replace(task);
        await rig.WalkAsync(engine, Utc(10, 20, 8, 1));
        rig.Replace(task with { Schedule = "0 * * * *" });
        await rig.WalkAsync(engine, Utc(10, 20, 8, 2));

        Assert.Single(rig.Executed);
        Assert.Single(rig.History);
    }

    [Fact]
    public async Task AStoredTaskListThatCannotBeRead_TurnsHealthRed()
    {
        var rig = new EngineRig(Utc(10, 14, 0, 0));
        rig.Storage.Set("tasks", "[{\"Id\": 1}]");
        using var engine = rig.Start();

        await engine.TickAsync();

        var health = engine.Health.Read();
        Assert.False(health.Healthy);
        Assert.Contains(health.Rows, row => row.Label == "The stored task list cannot be read; no task fires");
    }

    [Fact]
    public async Task TheHealthSection_TurnsUnhealthyPastTwoMissedTicks_AndHealthyOnTheNextTick()
    {
        var rig = new EngineRig(Utc(10, 14, 0, 0));
        using var engine = rig.Start();
        await engine.TickAsync();

        rig.Clock.Now += TimeSpan.FromSeconds(60);
        var twoMissed = engine.Health.Read().Healthy;
        rig.Clock.Now += TimeSpan.FromSeconds(1);
        var pastTwo = engine.Health.Read().Healthy;
        await engine.TickAsync();

        Assert.Equal((true, false, true), (twoMissed, pastTwo, engine.Health.Read().Healthy));
    }
}
