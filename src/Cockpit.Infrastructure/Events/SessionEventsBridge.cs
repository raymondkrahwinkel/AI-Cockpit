using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Events;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
using Microsoft.Extensions.Hosting;

namespace Cockpit.Infrastructure.Events;

// AC-1386: the session registry and every live pane's transcript rows, the assistant's included, onto the backend
// event log. The log holds every pane's events; `EventsEndpoint` keeps a reader to the panes its key may see.
internal sealed class SessionEventsBridge(ISessionRegistry registry, IBackendEventLog log) : IHostedService, ISingletonService
{
    private readonly Lock _gate = new();
    private readonly Dictionary<ISessionHandle, Action<TranscriptRowUpsert>> _watched = new(ReferenceEqualityComparer.Instance);

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
            foreach (var (handle, onRow) in _watched)
            {
                handle.RowUpserted -= onRow;
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
                gone.RowUpserted -= _watched[gone];
                _watched.Remove(gone);
            }

            foreach (var handle in live.Where(handle => !_watched.ContainsKey(handle)))
            {
                var paneId = handle.PaneId;
                // The event id is the log's own, drawn inside its gate so it only ever rises; the upsert's seq rides in the data.
                Action<TranscriptRowUpsert> onRow = upsert => log.Append("row", paneId, upsert);
                handle.RowUpserted += onRow;
                _watched[handle] = onRow;
            }
        }
    }
}
