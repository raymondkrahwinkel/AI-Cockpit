using System.Collections.Specialized;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Events;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
using Microsoft.Extensions.Hosting;

namespace Cockpit.Infrastructure.Events;

// AC-1386: the session registry and every live pane's transcript rows, the assistant's included, onto the backend
// event log. AC-1388: its live state and tool calls too, so a remote handle follows one ordered stream. The log holds
// every pane's events; `EventsEndpoint` keeps a reader to the panes its key may see.
internal sealed class SessionEventsBridge(ISessionRegistry registry, IBackendEventLog log) : IHostedService, ISingletonService
{
    private readonly Lock _gate = new();
    private readonly Dictionary<ISessionHandle, Action> _watched = new(ReferenceEqualityComparer.Instance);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        registry.Changed += _OnRegistryChanged;
        _Follow();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        registry.Changed -= _OnRegistryChanged;
        lock (_gate)
        {
            foreach (var unsubscribe in _watched.Values)
            {
                unsubscribe();
            }

            _watched.Clear();
        }

        return Task.CompletedTask;
    }

    private void _OnRegistryChanged(object? sender, EventArgs e)
    {
        _Follow();
        log.Append("sessions-changed", null, new { });
    }

    // Subscribes the panes that arrived and lets go of the ones that left, keyed by handle: a pane id registered again
    // is a new handle, and the old one must not keep writing under it.
    private void _Follow()
    {
        lock (_gate)
        {
            var live = registry.All.Append(registry.Assistant).OfType<ISessionHandle>().ToHashSet<ISessionHandle>(ReferenceEqualityComparer.Instance);
            foreach (var gone in _watched.Keys.Where(handle => !live.Contains(handle)).ToList())
            {
                _watched[gone]();
                _watched.Remove(gone);
            }

            foreach (var handle in live.Where(handle => !_watched.ContainsKey(handle)))
            {
                var paneId = handle.PaneId;
                // The event id is the log's own, drawn inside its gate so it only ever rises; the upsert's seq rides in the data.
                // So does the pane: an SSE frame carries no pane id, and a reader must know whose row it is.
                // Its scope is the pane's at this event, so the event stays readable to the same keys after the pane closes.
                void append(string kind, object data) => log.Append(kind, paneId, data, handle.ActiveProfileLabel, handle.ProjectId);
                Action<TranscriptRowUpsert> onRow = upsert => append("row", new { PaneId = paneId, upsert.Seq, upsert.Version, upsert.Row });
                // No more than the rows already carry: the session's folder and its CLI conversation id stay here.
                Action<SessionLiveState> onLiveState = state => append(
                    "live-state", new { PaneId = paneId, LiveState = state with { Connection = null, CliSessionId = null } });
                // The tool's output rides in its row, clamped to the row's budget; this event names the call only.
                Action<SessionToolCall> onTool = call => append("tool", new { PaneId = paneId, Call = call with { ResultContent = string.Empty } });
                var control = handle.Control;
                NotifyCollectionChangedEventHandler? onQueue = control is not null
                    ? (_, _) => append("queue", new
                    {
                        PaneId = paneId,
                        Queue = control.Queue.Select(prompt => new { prompt.WireId, prompt.Text }),
                    })
                    : null;
                handle.RowUpserted += onRow;
                handle.LiveStateChanged += onLiveState;
                handle.ToolActivityProduced += onTool;
                if (control is not null && onQueue is not null)
                {
                    control.QueueChanged += onQueue;
                }

                _watched[handle] = () =>
                {
                    handle.RowUpserted -= onRow;
                    handle.LiveStateChanged -= onLiveState;
                    handle.ToolActivityProduced -= onTool;
                    if (onQueue is not null && control is not null)
                    {
                        control.QueueChanged -= onQueue;
                    }
                };
            }
        }
    }
}
