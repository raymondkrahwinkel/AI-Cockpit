using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Assistant;

namespace Cockpit.Infrastructure.Sessions;

// The cockpit's answer to "which sessions are alive?" (AC-85), so neither the managed-worktrees panel nor
// `worktree_remove` can pull a running session's checkout out from under it. Fed by the session registry's panes
// (AC-1373) and every headless `ILiveSessionSource` (e.g. the delegation engine, AC-106), as one shared truth.
public sealed class LiveSessionRegistry(ISessionRegistry panes, IEnumerable<ILiveSessionSource> sources)
    : ILiveSessionRegistry, ISingletonService
{
    private readonly IReadOnlyList<ILiveSessionSource> _sources = [.. sources];

    public IReadOnlySet<string> LiveSessionIds
    {
        get
        {
            // Read afresh on every call rather than cached: a session that closed a moment ago must stop protecting
            // its worktree at once, or the guard outlives the thing it guards.
            var live = new HashSet<string>(StringComparer.Ordinal)
            {
                // AC-658: the assistant owns worktrees it makes but is excluded from _AllSessions() by
                // construction, so every consumer must still read it as live, or its worktree gets swept
                // as an orphan or removed by another session as "not live".
                AssistantIdentity.PaneId,
            };
            live.UnionWith(panes.All.Select(pane => pane.PaneId));

            foreach (var source in _sources)
            {
                live.UnionWith(source.LiveSessionIds);
            }

            return live;
        }
    }
}
