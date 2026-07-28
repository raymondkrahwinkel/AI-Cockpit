using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;

namespace Cockpit.App.Services;

/// <summary>
/// The cockpit's answer to "which sessions are alive?" (AC-85), read by the worktree removal paths so neither the
/// managed-worktrees panel nor an agent's <c>worktree_remove</c> can pull a running session's checkout out from under
/// it. Two kinds of session feed it: the cockpit's panes, which the view model points at through <see cref="SetSource"/>,
/// and every headless <see cref="ILiveSessionSource"/> the container knows — today the delegation engine, whose tasks
/// run without a pane and so were invisible here (AC-106). A shared singleton, so the panel and the MCP tools read one
/// truth. Reports none until something feeds it (a headless run with no live UI and no delegated task), where the
/// startup reconcile is the net instead.
/// </summary>
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
            var live = new HashSet<string>(StringComparer.Ordinal);
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

    /// <summary>Points the registry at the cockpit's live pane ids; called once as the cockpit view model is built.</summary>
    public void SetSource(Func<IReadOnlySet<string>> source) => _panes = source;
}
