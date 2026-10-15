using Cockpit.Plugin.Schedules.Contracts;

namespace Cockpit.Plugin.Schedules.Engine;

// The engine (plan §4): one tick that compares "now" with each task's mark, the last moment it dealt with. A jump
// forward is sleep, hibernate or a restart, and the slots in between are missed; a jump back finds no slot past the
// mark. Follows Workflows' ScheduleCatchUp in meaning: the mark is written before the action, and one catch-up at most.
internal sealed class ScheduleEngine : IDisposable
{
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);
    public const string StillRunning = "still running";

    // One tick of lag is the ordinary gap between a slot and the tick that sees it, not lateness.
    private static readonly TimeSpan OnTimeWindow = TimeSpan.FromMinutes(1);

    // ponytail: a week asleep with an every-minute task logs only the newest slots one by one, the rest as a count.
    private const int MaxMissedRecords = 50;

    private readonly TaskStore _tasks;
    private readonly MarkStore _marks;
    private readonly RunStore _runs;
    private readonly Func<ScheduledTask, CancellationToken, Task<ActionResult>> _execute;
    private readonly Func<int> _maxConcurrentRuns;
    private readonly TimeProvider _time;
    private readonly ITimer _timer;

    private readonly Lock _tickGate = new();
    private readonly Lock _runGate = new();
    private readonly HashSet<string> _busy = [];
    private readonly Queue<TaskCompletionSource> _waiting = new();
    private int _active;

    public ScheduleEngine(
        TaskStore tasks,
        MarkStore marks,
        RunStore runs,
        Func<ScheduledTask, CancellationToken, Task<ActionResult>> execute,
        Func<int> maxConcurrentRuns,
        TimeProvider time)
    {
        _tasks = tasks;
        _marks = marks;
        _runs = runs;
        _execute = execute;
        _maxConcurrentRuns = maxConcurrentRuns;
        _time = time;

        _runs.FailInterrupted();
        Health = new EngineHealth(time, Tick);
        _timer = time.CreateTimer(_ => _OnTimer(), null, TimeSpan.Zero, Tick);
    }

    public EngineHealth Health { get; }

    // Completes when every run this tick started is done; the timer does not wait for it, tests do.
    public Task TickAsync()
    {
        var now = _time.GetUtcNow();
        var started = new List<Task>();
        var unusable = 0;
        var tasks = _tasks.Load();

        lock (_tickGate)
        {
            var armed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var task in (tasks ?? []).Where(task => task.IsEnabled))
            {
                if (!TimeZoneInfo.TryFindSystemTimeZoneById(task.TimeZone, out var zone)
                    || TaskSchedule.TryParse(task.Schedule, zone) is not { } schedule)
                {
                    unusable++;
                    continue;
                }

                try
                {
                    armed.Add(task.Id);
                    if (_Due(task, schedule, zone, now) is { } run)
                    {
                        started.Add(run);
                    }
                }
                catch (Exception)
                {
                    // One task's storage failure must not stop the others; health counts it.
                    unusable++;
                }
            }

            // A task off, gone or unreadable loses its mark, so the time it was away is not owed when it returns.
            if (tasks is not null)
            {
                _marks.Retain(armed);
            }
        }

        // Last, so a tick that throws part-way never counts as one that ran.
        Health.Beat(unusable, isTaskListReadable: tasks is not null);
        return Task.WhenAll(started);
    }

    public void Dispose() => _timer.Dispose();

    private void _OnTimer()
    {
        try
        {
            _ = TickAsync();
        }
        catch (Exception)
        {
            // A throw on a timer thread takes the process down. The heartbeat stays put, which turns /healthz red.
        }
    }

    private Task? _Due(ScheduledTask task, TaskSchedule schedule, TimeZoneInfo zone, DateTimeOffset now)
    {
        // A task seen for the first time, or with a new schedule, starts counting now: armed at 09:05, it does not
        // owe the 09:00 slot.
        if (_marks.For(task.Id) is not { } mark || mark.Schedule != task.Schedule || mark.TimeZone != task.TimeZone)
        {
            _marks.Set(task.Id, new TaskMark(now, task.Schedule, task.TimeZone));
            return null;
        }

        var slots = schedule.Slots(mark.At, now, zone).ToList();
        if (slots.Count == 0)
        {
            return null;
        }

        var slot = slots[^1];
        _marks.Set(task.Id, mark with { At = slot });

        // The newest slot is always less than one period old, or the next one would be here too: the plan's limit
        // on a catch-up's age holds by construction. A one-off has no next slot and may always catch up.
        RunTrigger? trigger = now - slot <= OnTimeWindow
            ? RunTrigger.Schedule
            : task.MissedRuns == MissedRunPolicy.RunOnceWhenBack ? RunTrigger.CatchUp : null;

        _RecordMissed(task, trigger is null ? slots : slots[..^1], now);
        return trigger is { } fired ? _Start(task, slot, fired, now) : null;
    }

    private void _RecordMissed(ScheduledTask task, List<DateTimeOffset> slots, DateTimeOffset now)
    {
        var logged = slots.TakeLast(MaxMissedRecords).ToList();
        if (slots.Count > logged.Count)
        {
            _runs.Add(_Record(task, slots[0], RunTrigger.Schedule, RunOutcome.Missed, now) with
            {
                Reason = $"{slots.Count - logged.Count} earlier slots missed, from {slots[0]:u}",
            });
        }

        foreach (var slot in logged)
        {
            _runs.Add(_Record(task, slot, RunTrigger.Schedule, RunOutcome.Missed, now));
        }
    }

    private Task? _Start(ScheduledTask task, DateTimeOffset slot, RunTrigger trigger, DateTimeOffset now)
    {
        lock (_runGate)
        {
            if (!_busy.Add(task.Id))
            {
                _runs.Add(_Record(task, slot, trigger, RunOutcome.Skipped, now) with { Reason = StillRunning });
                return null;
            }
        }

        var record = _Record(task, slot, trigger, RunOutcome.Running, now);
        try
        {
            _runs.Add(record);
        }
        catch (Exception)
        {
            lock (_runGate)
            {
                _busy.Remove(task.Id);
            }

            throw;
        }

        return _RunAsync(task, record);
    }

    private async Task _RunAsync(ScheduledTask task, RunRecord record)
    {
        try
        {
            await _AcquireSlotAsync();
            var started = _time.GetUtcNow();
            RunRecord done;
            try
            {
                var result = await _execute(task, CancellationToken.None);
                done = record with { Outcome = result.Outcome, Output = result.Output, Reason = result.Reason };
            }
            catch (Exception exception)
            {
                done = record with { Outcome = RunOutcome.Failed, Reason = exception.Message };
            }
            finally
            {
                _ReleaseSlot();
            }

            _runs.Replace(done with { Duration = _time.GetUtcNow() - started });
        }
        catch (Exception)
        {
            // The history could not be written. The slot is marked, so the run reads as interrupted after a restart.
        }
        finally
        {
            lock (_runGate)
            {
                _busy.Remove(task.Id);
            }
        }
    }

    private Task _AcquireSlotAsync()
    {
        var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_runGate)
        {
            _waiting.Enqueue(waiter);
            _Pump();
        }

        return waiter.Task;
    }

    private void _ReleaseSlot()
    {
        lock (_runGate)
        {
            _active--;
            _Pump();
        }
    }

    // First come, first served, against the maximum as it reads now, on every acquire and every release.
    private void _Pump()
    {
        while (_active < _maxConcurrentRuns() && _waiting.TryDequeue(out var next))
        {
            _active++;
            next.SetResult();
        }
    }

    private static RunRecord _Record(ScheduledTask task, DateTimeOffset slot, RunTrigger trigger, RunOutcome outcome, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid().ToString("n"),
        TaskId = task.Id,
        ScheduledFor = slot,
        Trigger = trigger,
        Outcome = outcome,
        StartedAt = now,
    };
}
