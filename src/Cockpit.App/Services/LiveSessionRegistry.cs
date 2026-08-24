using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Assistant;

namespace Cockpit.App.Services;

// The cockpit's answer to "which sessions are alive?" (AC-85), so neither the managed-worktrees panel nor
// `worktree_remove` can pull a running session's checkout out from under it. Fed by the cockpit's panes
// and every headless `ILiveSessionSource` (e.g. the delegation engine, AC-106), as one shared truth.
public sealed class LiveSessionRegistry : ILiveSessionRegistry, ISingletonService
{
    private readonly IReadOnlyList<ILiveSessionSource> _sources;

    private Func<IReadOnlySet<string>>? _panes;

    public LiveSessionRegistry(IEnumerable<ILiveSessionSource> sources) => _sources = [.. sources];

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
            if (_panes is { } panes)
            {
                live.UnionWith(panes());
            }

            foreach (var source in _sources)
            {
                live.UnionWith(source.LiveSessionIds);
            }

            return live;
        }
    }

    // Points the registry at the cockpit's live pane ids; called once as the cockpit view model is built.
    public void SetSource(Func<IReadOnlySet<string>> source) => _panes = source;
}
