using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Assistant;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Infrastructure.Sessions;

// AC-1706: a session the assistant started that ends a turn without a closing (AC-1705) gets one nudge in its own
// inbox, which AC-656's wake turns into a turn; a second stall before a closed turn goes to the assistant as
// `stalled`. SDK only: it hangs on the turn end (AC-294). Closings are structural, never read from the wording.
public sealed class StallNudger : ISingletonService, IDisposable
{
    public const string NudgeKind = "stall";

    public const string Nudge =
        "Your turn ended without a closing. Carry on with the step you last announced, or report done, blocked or " +
        "your question.";

    // The same sender SessionWatcher reports under: the cockpit noticed this, not a neighbour.
    private const string SenderPaneId = "cockpit-session-watch";

    private const int TailRows = 5;

    private readonly IAgentMessageInbox _inbox;
    private readonly ILogger<StallNudger> _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<ISessionHandle, Followed> _followed = new(ReferenceEqualityComparer.Instance);
    private ISessionRegistry? _registry;

    public StallNudger(IAgentMessageInbox inbox, ILogger<StallNudger>? logger = null)
    {
        _inbox = inbox;
        _logger = logger ?? NullLogger<StallNudger>.Instance;
    }

    // One pane's turn so far, and whether it was already nudged for this piece of work.
    private sealed class Followed
    {
        public Lock Gate { get; } = new();

        public bool Closed { get; set; }

        public bool Nudged { get; set; }

        public Action Unsubscribe { get; set; } = () => { };
    }

    // Follows every session in `registry`, the assistant left out (`All` does not carry it). Idempotent.
    public void Start(ISessionRegistry registry)
    {
        if (_registry is not null)
        {
            return;
        }

        _registry = registry;
        registry.Changed += _OnRegistryChanged;
        _Follow();
    }

    private void _OnRegistryChanged(object? sender, EventArgs e) => _Follow();

    private void _Follow()
    {
        if (_registry is not { } registry)
        {
            return;
        }

        lock (_gate)
        {
            var live = registry.All.ToHashSet<ISessionHandle>(ReferenceEqualityComparer.Instance);
            foreach (var gone in _followed.Keys.Where(handle => !live.Contains(handle)).ToList())
            {
                _followed[gone].Unsubscribe();
                _followed.Remove(gone);
            }

            foreach (var handle in live.Where(handle => handle.StartedByTheAssistant && !_followed.ContainsKey(handle)))
            {
                var followed = new Followed();
                Action<SessionToolCall> onTool = call => _OnTool(followed, call.ToolName);
                Action<SessionTurnEnd> onTurnEnded = end => _ = _OnTurnEndedAsync(handle, followed, end);
                handle.ToolActivityProduced += onTool;
                handle.TurnEnded += onTurnEnded;
                followed.Unsubscribe = () =>
                {
                    handle.ToolActivityProduced -= onTool;
                    handle.TurnEnded -= onTurnEnded;
                };
                _followed[handle] = followed;
            }
        }
    }

    // Closed only when the turn's last tool call is a notify or a start_run (whose verdict wakes it); any other tool
    // after it reopens the turn. set_status neither closes nor reopens: AC-1681 notified, set its status, appended to
    // Depot and stalled.
    private static void _OnTool(Followed followed, string toolName)
    {
        if (_IsTool(toolName, "set_status"))
        {
            return;
        }

        lock (followed.Gate)
        {
            followed.Closed = followed.Closed || _IsTool(toolName, "notify") || _IsTool(toolName, "start_run");
        }
    }

    // Claude names an MCP tool `mcp__<server>__<tool>`, Codex the bare tool.
    private static bool _IsTool(string toolName, string tool) =>
        string.Equals(toolName, tool, StringComparison.Ordinal) || toolName.EndsWith("__" + tool, StringComparison.Ordinal);

    private async Task _OnTurnEndedAsync(ISessionHandle handle, Followed followed, SessionTurnEnd end)
    {
        try
        {
            bool closedByTool;
            lock (followed.Gate)
            {
                closedByTool = followed.Closed;
                followed.Closed = false;
            }

            // A broken or interrupted turn is not a stall: the error or the operator already said why it stopped.
            if (end.IsError || end.WasInterrupted)
            {
                return;
            }

            var closed = closedByTool || await _IsWaitingOnSomethingAsync(handle).ConfigureAwait(false);
            bool nudge;
            lock (followed.Gate)
            {
                if (closed)
                {
                    followed.Nudged = false;
                    return;
                }

                nudge = !followed.Nudged;
                followed.Nudged = nudge;
            }

            if (nudge)
            {
                _logger.LogInformation("Session {Pane} ended a turn without a closing; nudged once.", handle.PaneId);
                _inbox.Deliver(SenderPaneId, handle.PaneId, NudgeKind, Nudge);
                return;
            }

            _logger.LogInformation("Session {Pane} stalled again after its nudge; reported to the assistant.", handle.PaneId);
            _inbox.Deliver(
                SenderPaneId,
                AssistantIdentity.PaneId,
                "session",
                $"Watched session '{handle.Title}' ({handle.PaneId}) — stalled: it ended a turn without a closing twice, "
                    + $"the second time after a nudge to carry on.{await _TailAsync(handle).ConfigureAwait(false)}\n"
                    + "Nothing more has been started about it.",
                subjectPaneId: handle.PaneId);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Judging the end of a turn on {Pane} failed.", handle.PaneId);
        }
    }

    // An open question or permission, or work of its own still running: the session is not finished talking.
    private static async Task<bool> _IsWaitingOnSomethingAsync(ISessionHandle handle)
    {
        var wake = await handle.ReadWakeStateAsync().ConfigureAwait(false);
        return wake.HasPendingConsent
            || wake.SessionStatus is SessionStatus.NeedsAttention or SessionStatus.WorkingBackground
            || handle.LiveState.BackgroundTasks.Count > 0
            || (await handle.ReadPendingPermissionsAsync().ConfigureAwait(false)).Count > 0
            || await handle.HasOutstandingBackgroundShellsAsync().ConfigureAwait(false);
    }

    private static async Task<string> _TailAsync(ISessionHandle handle)
    {
        if (!handle.HasReadableTranscript)
        {
            return string.Empty;
        }

        var slice = await handle.ReadTranscriptAsync(TailRows).ConfigureAwait(false);
        return string.Concat(slice.Entries.Select(entry => "\n> " + _Short(entry.Text)));
    }

    private static string _Short(string row)
    {
        var single = row.ReplaceLineEndings(" ").Trim();
        return single.Length <= 200 ? single : single[..200] + "…";
    }

    public void Dispose()
    {
        if (_registry is { } registry)
        {
            registry.Changed -= _OnRegistryChanged;
        }

        lock (_gate)
        {
            foreach (var followed in _followed.Values)
            {
                followed.Unsubscribe();
            }

            _followed.Clear();
            _registry = null;
        }
    }
}
