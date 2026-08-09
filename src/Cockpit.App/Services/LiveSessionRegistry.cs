using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Assistant;

namespace Cockpit.App.Services;

// The cockpit's answer to "which sessions are alive?" (AC-85), read by the worktree removal paths so neither the
// managed-worktrees panel nor an agent's `worktree_remove` can pull a running session's checkout out from under
// it. Two kinds of session feed it: the cockpit's panes, which the view model points at through `SetSource`,
// and every headless `ILiveSessionSource` the container knows — today the delegation engine, whose tasks
// run without a pane and so were invisible here (AC-106). A shared singleton, so the panel and the MCP tools read one
// truth. Reports none until something feeds it (a headless run with no live UI and no delegated task), where the
// startup reconcile is the net instead.
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
                // AC-658: the assistant owns every worktree it makes with worktree_create and is in no session list
                // by construction (_AllSessions() deliberately excludes it), so every consumer of this registry —
                // WorktreeReconciler's sweep and worktree_remove alike — must read it as live, or a worktree it is
                // actively working in is either swept as an orphan or removed by another session as "not live".
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
