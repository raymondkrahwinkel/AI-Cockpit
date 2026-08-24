using Avalonia.Threading;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.App.Services;

// AC-656: gives a pane a turn as soon as mail is waiting in its own inbox, rather than leaving it for that
// pane's next turn or tool call (AC-394/AC-527), or requiring opt-in like AC-395's urgent notify does.
// Every live pane is checked every tick, no `watch_session` arming step needed, same shape as CiWatcher/SessionWatcher.
public sealed class InboxWakeScheduler(
    IAgentMessageInbox inbox,
    IWorkspaceAgentGateway gateway,
    ILogger<InboxWakeScheduler>? logger = null) : ISingletonService, IDisposable
{
    // Short enough that "you have mail" beats waiting for a passive next-turn or tool-call pickup, cheap enough to
    // afford at that rate: a tick reads collections the UI already holds and calls the gateway only for a pane that
    // actually has something waiting.
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly ILogger<InboxWakeScheduler> _logger = logger ?? NullLogger<InboxWakeScheduler>.Instance;

    // The oldest waiting message id last attempted per pane. The wake send is fire-and-forget (see
    // WorkspaceAgentGateway._SendWakeAsync), so without this a slow send could be re-attempted before the
    // first landed. Cleared once PeekOldest no longer returns that message.
    private readonly Dictionary<string, string> _attempted = new(StringComparer.Ordinal);

    private DispatcherTimer? _timer;
    private bool _disposed;

    // Every pane worth checking on this tick. Replaced by the tests, which have no cockpit and no UI thread.
    public Func<IReadOnlyList<string>>? Panes { get; set; }

    // Starts watching the clock. Idempotent, and built on the UI thread — that is where the session list is read and
    // where a DispatcherTimer has to be created to ever tick at all (AC-368).
    public void Start()
    {
        if (_timer is not null || _disposed)
        {
            return;
        }

        _timer = new DispatcherTimer { Interval = Interval };
        _timer.Tick += _OnTick;
        _timer.Start();

        _ = RunOnceAsync();
    }

    // One look at every live pane's inbox. Public because the tests drive it directly rather than waiting on the
    // timer — the same seam CiWatcher/SessionWatcher open.
    public async Task RunOnceAsync()
    {
        if (Panes is null)
        {
            return;
        }

        var live = Panes();
        if (live.Count == 0)
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var paneId in live)
        {
            if (!seen.Add(paneId))
            {
                continue;
            }

            await _LookAsync(paneId).ConfigureAwait(true);
        }

        // Nothing remembered here for a pane that is no longer live, so one that closes with a wake still attempted
        // for it does not block a later pane reusing the id (the assistant's is fixed) from ever being tried.
        foreach (var stale in _attempted.Keys.Where(paneId => !seen.Contains(paneId)).ToList())
        {
            _attempted.Remove(stale);
        }
    }

    private async Task _LookAsync(string paneId)
    {
        if (inbox.PeekOldest(paneId) is not { } message)
        {
            _attempted.Remove(paneId);
            return;
        }

        if (_attempted.TryGetValue(paneId, out var last) && string.Equals(last, message.Id, StringComparison.Ordinal))
        {
            return;
        }

        AgentWakeOutcome outcome;
        try
        {
            outcome = await gateway.TryWakeForWaitingMailAsync(message.FromPaneId, paneId, message.Kind).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Waking {Pane} for waiting mail failed; the next tick tries again.", paneId);
            return;
        }

        if (outcome != AgentWakeOutcome.Woken)
        {
            // Busy, awaiting its operator, gone, and so on are not news every 30 seconds — the same reason CiWatcher
            // stays quiet on an unchanged red check. The next tick tries again for free; nothing is remembered here
            // to skip that retry, because an outcome like Busy is exactly the kind that stops applying on its own.
            return;
        }

        _attempted[paneId] = message.Id;
        _logger.LogInformation("Gave {Pane} a turn for mail waiting from {From}.", paneId, message.FromPaneId);
    }

    private async void _OnTick(object? sender, EventArgs e)
    {
        try
        {
            await RunOnceAsync();
        }
        catch (Exception exception)
        {
            // A watcher must never be the reason the cockpit falls over, but it must leave a trace — a failure that
            // stops the loop silently is a watcher that never wakes anyone again.
            _logger.LogError(exception, "An inbox wake tick failed; the next one will try again.");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Panes = null;
        _attempted.Clear();

        if (_timer is null)
        {
            return;
        }

        _timer.Stop();
        _timer.Tick -= _OnTick;
        _timer = null;
    }
}
