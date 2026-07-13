using System.Text.RegularExpressions;
using Avalonia.Threading;
using Cockpit.Plugin.Workflows.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.Workflows.Engine;

/// <summary>
/// What makes a flow more than a button (#69): the triggers that fire by themselves. A flow marked Active and
/// starting with "Text appears" runs when a session says the thing; one starting with "Schedule" runs when the clock
/// comes round.
/// <para>
/// Only an <em>active</em> flow fires. A flow being drawn must not run while it is half-wired, and Active is the
/// switch that says "I meant this" — which is why nothing here reads a flow that is merely saved.
/// </para>
/// <para>
/// The flows are re-read on every signal rather than cached. They are edited in a dialog that this object cannot see,
/// and a watcher running yesterday's copy of a flow is worse than one that is a millisecond late.
/// </para>
/// </summary>
internal sealed class FlowWatcher : IDisposable
{
    // The clock is checked every half minute: a schedule of "09:00" means that minute, not that second, and a timer
    // that wakes 120 times a minute to look at a list of two flows is a bonfire made of a laptop battery.
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    private readonly WorkflowStore _store;
    private readonly RunStore _runs;
    private readonly ICockpitHost _host;
    private readonly DispatcherTimer _timer;

    // Built on the first firing, not in the constructor: plugins initialise in an order nobody controls, and an
    // engine built at startup would not have YouTrack's steps in it — a flow that moves a ticket would then be
    // "skipped, this cockpit cannot run youtrack.start", which is a lie about what this build can do.
    private WorkflowEngine? _engine;

    // What has already fired for a given minute, so a schedule does not run twice within the same one.
    private readonly HashSet<string> _firedThisMinute = [];
    private DateTimeOffset _minute = DateTimeOffset.MinValue;

    // A trigger already running. A flow that watches for text and then *sends* text to a session feeds its own
    // trigger, and the cockpit would sit there running it forever — the worst thing this could do, because it looks
    // from the outside like the app has simply gone busy.
    private readonly HashSet<string> _running = [];

    private bool _disposed;

    public FlowWatcher(WorkflowStore store, RunStore runs, ICockpitHost host)
    {
        _store = store;
        _runs = runs;
        _host = host;

        _host.Sessions.OutputProduced += _OnOutput;
        _host.WorkflowTriggerRaised += _OnPluginTrigger;

        _timer = new DispatcherTimer { Interval = Tick };
        _timer.Tick += (_, _) => _OnClock(DateTimeOffset.Now);
        _timer.Start();
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
        _timer.Stop();
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

    // The clock came round. "09:00" fires in that minute; "every 15m" fires when the minute divides.
    private void _OnClock(DateTimeOffset now)
    {
        var minute = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset);
        if (minute != _minute)
        {
            _minute = minute;
            _firedThisMinute.Clear();
        }

        foreach (var (workflow, trigger) in _Triggers("cockpit.schedule"))
        {
            var when = trigger.Parameters.GetValueOrDefault("When");
            if (string.IsNullOrWhiteSpace(when) || !Schedule.IsDue(when, now))
            {
                continue;
            }

            // Two ticks fall inside the same minute, and "09:00" is a minute, not an instant.
            if (!_firedThisMinute.Add($"{workflow.Id}:{trigger.Id}"))
            {
                continue;
            }

            _ = _FireAsync(workflow, trigger,
            [
                WorkflowItem.Of(new Dictionary<string, string>
                {
                    ["at"] = now.ToString("yyyy-MM-dd HH:mm"),
                }),
            ]);
        }
    }

    // Read fresh: the flows are edited in a dialog this object cannot see.
    private IEnumerable<(Workflow Workflow, WorkflowNode Trigger)> _Triggers(string typeId) =>
        _store.Load()
            .Where(workflow => workflow.IsActive)
            .SelectMany(workflow => workflow.Nodes
                .Where(node => node.TypeId == typeId && !node.IsDisabled)
                .Select(node => (workflow, node)));

    private async Task _FireAsync(Workflow workflow, WorkflowNode trigger, IReadOnlyList<WorkflowItem> seed)
    {
        var key = $"{workflow.Id}:{trigger.Id}";
        if (!_running.Add(key))
        {
            return;
        }

        _engine ??= EngineFactory.Create(_host, _host.WorkflowSteps);

        WorkflowRun run;
        try
        {
            run = await _engine.RunAsync(workflow, trigger.Id, seed);
        }
        finally
        {
            _running.Remove(key);
        }

        _runs.Add(run);

        // A flow that fired and failed while you were looking elsewhere must say so — this is the one thing that
        // separates automation from a machine quietly doing nothing.
        if (run.Status == RunStatus.Failed)
        {
            _host.ShowToast($"'{workflow.Name}' failed: {run.Error}", Cockpit.Plugins.Abstractions.Notifications.PluginToastSeverity.Warning);
        }
    }

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
