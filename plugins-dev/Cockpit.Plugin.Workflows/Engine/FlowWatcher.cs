using System.Text.RegularExpressions;
using Cockpit.Plugin.Workflows.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.Workflows.Engine;

// What makes a flow more than a button (#69): the triggers that fire by themselves. A flow marked Active and
// starting with "Text appears" runs when a session says the thing; one starting with "Schedule" runs when the clock
// comes round.
//
// Only an *active* flow fires. A flow being drawn must not run while it is half-wired, and Active is the
// switch that says "I meant this" — which is why nothing here reads a flow that is merely saved.
//
// The flows are re-read on every signal rather than cached. They are edited in a dialog that this object cannot see,
// and a watcher running yesterday's copy of a flow is worse than one that is a millisecond late.
//
// The clock is `TimeProvider`, not Avalonia's dispatcher (#AC-1359): a headless cockpit has no dispatcher to tick
// on, and a schedule this plugin can only keep on the desktop is not a schedule a server can run.
internal sealed class FlowWatcher : IDisposable
{
    // The clock is checked every half minute: a schedule of "09:00" means that minute, not that second, and a timer
    // that wakes 120 times a minute to look at a list of two flows is a bonfire made of a laptop battery.
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    private readonly WorkflowStore _store;
    private readonly RunStore _runs;
    private readonly ScheduleMarks _marks;
    private readonly ICockpitHost _host;
    private readonly Func<TimeSpan> _grace;
    private readonly TimeProvider _time;
    private readonly ITimer _timer;

    // Built on the first firing, not in the constructor: plugins initialise in an order nobody controls, and an
    // engine built at startup would not have YouTrack's steps in it — a flow that moves a ticket would then be
    // "skipped, this cockpit cannot run youtrack.start", which is a lie about what this build can do.
    private WorkflowEngine? _engine;

    // A trigger already running, guarded by `_runningLock` now that the clock's own tick can land on a threadpool
    // thread too. A flow that watches for text and then *sends* text to a session feeds its own trigger, and the
    // cockpit would sit there running it forever — which looks from the outside like the app has simply gone busy.
    private readonly HashSet<string> _running = [];
    private readonly Lock _runningLock = new();

    private bool _disposed;

    public FlowWatcher(WorkflowStore store, RunStore runs, ScheduleMarks marks, ICockpitHost host, Func<TimeSpan> grace)
        : this(store, runs, marks, host, grace, TimeProvider.System)
    {
    }

    // For tests: a clock that only moves when told to, so a catch-up decision does not need a real restart to prove.
    internal FlowWatcher(WorkflowStore store, RunStore runs, ScheduleMarks marks, ICockpitHost host, Func<TimeSpan> grace, TimeProvider time)
    {
        _store = store;
        _runs = runs;
        _marks = marks;
        _host = host;
        _grace = grace;
        _time = time;

        _host.Sessions.OutputProduced += _OnOutput;
        _host.WorkflowTriggerRaised += _OnPluginTrigger;

        _timer = _time.CreateTimer(_ => _OnClock(_time.GetUtcNow()), null, TimeSpan.Zero, Tick);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _host.Sessions.OutputProduced -= _OnOutput;
        _host.WorkflowTriggerRaised -= _OnPluginTrigger;
        _timer.Dispose();
    }

    // A plugin fired one of its own triggers: a ticket was picked for a session, a review was requested. Every active
    // flow that begins with that trigger runs, starting with the data the plugin handed over.
    private void _OnPluginTrigger(object? sender, Cockpit.Plugins.Abstractions.Workflows.WorkflowTriggerFired fired)
    {
        foreach (var (workflow, trigger) in _Triggers(fired.TypeId))
        {
            _ = _FireAsync(workflow, trigger, [WorkflowItem.Of(fired.Data)]);
        }
    }

    // A session said something. Every active flow watching for text gets a look at it.
    private void _OnOutput(object? sender, SessionOutputText output)
    {
        foreach (var (workflow, trigger) in _Triggers("cockpit.text-match"))
        {
            var pattern = trigger.Parameters.GetValueOrDefault("Pattern");
            if (string.IsNullOrWhiteSpace(pattern) || !_Matches(output.Text, pattern))
            {
                continue;
            }

            _ = _FireAsync(workflow, trigger,
            [
                WorkflowItem.Of(new Dictionary<string, string>
                {
                    ["match"] = pattern,
                    ["text"] = output.Text.Trim(),
                    ["session"] = output.WorkingDirectory ?? string.Empty,
                }),
            ]);
        }
    }

    // The clock came round. Each schedule trigger is judged against its own marker: fire on time, catch up late
    // within the grace, or log a miss and leave it alone — never silently shifted (#AC-493), and never twice for
    // the same slot (#AC-1359).
    private void _OnClock(DateTimeOffset now)
    {
        foreach (var (workflow, trigger) in _Triggers("cockpit.schedule"))
        {
            var when = trigger.Parameters.GetValueOrDefault("When");
            if (string.IsNullOrWhiteSpace(when))
            {
                continue;
            }

            if (Schedule.ResolveZone(trigger.Parameters.GetValueOrDefault("Time zone")) is not { } zone)
            {
                continue;
            }

            var isOnce = when.TrimStart().StartsWith("once", StringComparison.OrdinalIgnoreCase);
            var previous = Schedule.Previous(when, zone, now);
            var marker = _marks.For(workflow.Id, trigger.Id);

            // A `once` slot has no next occurrence to wait for, so there is nothing to bootstrap: its one sighting
            // must be judged for real (Fire/Late/Missed) rather than seeded away like a recurring schedule's stale
            // first tick — seeding it here would mean it never runs at all.
            var effectiveMarker = marker ?? (isOnce ? DateTimeOffset.MinValue : null);
            var decision = ScheduleCatchUp.Decide(previous, effectiveMarker, now, _grace());

            if (decision.Outcome == ScheduleCatchUpOutcome.Nothing)
            {
                if (marker is null && previous is { } seed)
                {
                    _marks.Set(workflow.Id, trigger.Id, seed);
                }

                continue;
            }

            // Set before anything runs: at-most-once means a crash mid-run must not replay this slot on restart.
            _marks.Set(workflow.Id, trigger.Id, decision.Slot);

            if (decision.Outcome == ScheduleCatchUpOutcome.Missed)
            {
                _runs.Add(_MissedRun(workflow, decision.Slot, now));
            }
            else
            {
                var note = decision.Outcome == ScheduleCatchUpOutcome.Late
                    ? $"Late — scheduled for {decision.Slot:yyyy-MM-dd HH:mm} UTC."
                    : null;

                _ = _FireAsync(workflow, trigger,
                [
                    WorkflowItem.Of(new Dictionary<string, string>
                    {
                        ["at"] = decision.Slot.ToString("yyyy-MM-dd HH:mm"),
                    }),
                ], note);
            }

            // A one-shot slot is spent the moment it is handled — fired, late, or missed makes no difference: none
            // of the three means "ask again tomorrow".
            if (isOnce)
            {
                _Deactivate(workflow);
            }
        }
    }

    // Read fresh: the flows are edited in a dialog this object cannot see.
    private IEnumerable<(Workflow Workflow, WorkflowNode Trigger)> _Triggers(string typeId) =>
        _store.Load()
            .Where(workflow => workflow.IsActive)
            .SelectMany(workflow => workflow.Nodes
                .Where(node => node.TypeId == typeId && !node.IsDisabled)
                .Select(node => (workflow, node)));

    private void _Deactivate(Workflow workflow)
    {
        var workflows = _store.Load().ToList();
        var index = workflows.FindIndex(existing => existing.Id == workflow.Id);
        if (index < 0)
        {
            return;
        }

        workflows[index].IsActive = false;
        _store.Save(workflows);
    }

    private async Task _FireAsync(Workflow workflow, WorkflowNode trigger, IReadOnlyList<WorkflowItem> seed, string? note = null)
    {
        var key = $"{workflow.Id}:{trigger.Id}";

        lock (_runningLock)
        {
            if (!_running.Add(key))
            {
                _runs.Add(_SkippedRun(workflow, "Skipped — the previous run of this flow was still going."));
                return;
            }
        }

        _engine ??= EngineFactory.Create(_host, _host.WorkflowSteps);

        WorkflowRun run;
        try
        {
            run = await _engine.RunAsync(workflow, trigger.Id, RunOrigin.Trigger, seed);
        }
        finally
        {
            lock (_runningLock)
            {
                _running.Remove(key);
            }
        }

        run.Note = note;
        _runs.Add(run);

        // A flow that fired and failed while you were looking elsewhere must say so — this is the one thing that
        // separates automation from a machine quietly doing nothing.
        if (run.Status == RunStatus.Failed)
        {
            _host.ShowToast($"'{workflow.Name}' failed: {run.Error}", Cockpit.Plugins.Abstractions.Notifications.PluginToastSeverity.Warning);
        }
    }

    private static WorkflowRun _SkippedRun(Workflow workflow, string note) => new()
    {
        Id = Guid.NewGuid().ToString("n"),
        WorkflowId = workflow.Id,
        WorkflowName = workflow.Name,
        StartedAt = DateTimeOffset.UtcNow,
        FinishedAt = DateTimeOffset.UtcNow,
        Status = RunStatus.Skipped,
        Note = note,
    };

    private static WorkflowRun _MissedRun(Workflow workflow, DateTimeOffset slot, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid().ToString("n"),
        WorkflowId = workflow.Id,
        WorkflowName = workflow.Name,
        StartedAt = DateTimeOffset.UtcNow,
        FinishedAt = DateTimeOffset.UtcNow,
        Status = RunStatus.Skipped,
        Note = $"Missed — scheduled for {slot:yyyy-MM-dd HH:mm} UTC, this cockpit was not running until {now:yyyy-MM-dd HH:mm} UTC.",
    };

    // A pattern is plain text, unless it is written as a regex (/like this/) — the everyday case is "did it say
    // 'tests passed'", and making that person write a regex is a tax on the common case.
    private static bool _Matches(string text, string pattern)
    {
        var trimmed = pattern.Trim();

        if (trimmed.Length > 2 && trimmed.StartsWith('/') && trimmed.EndsWith('/'))
        {
            try
            {
                return Regex.IsMatch(text, trimmed[1..^1], RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
            }
            catch (ArgumentException)
            {
                // A pattern that is not a regex is not a reason to take the cockpit down. It simply never matches,
                // and the flow's own run history will show it never fired.
                return false;
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        return text.Contains(trimmed, StringComparison.OrdinalIgnoreCase);
    }
}
