using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;

namespace Cockpit.App.Services;

/// <summary>
/// The cockpit's answer to "which sessions are alive?" (AC-85), read by the worktree removal paths so neither the
/// managed-worktrees panel nor an agent's <c>worktree_remove</c> can pull a running session's checkout out from under
/// it. The pane ids live on the cockpit view model, which feeds them in through <see cref="SetSource"/>; a shared
/// singleton so the panel and the MCP tools read one truth. Reports none until a source is set (a headless run with
/// no live UI), where the startup reconcile is the net instead.
/// </summary>
public sealed class LiveSessionRegistry : ILiveSessionRegistry, ISingletonService
{
    private Func<IReadOnlySet<string>>? _source;

    public IReadOnlySet<string> LiveSessionIds =>
        _source?.Invoke() ?? new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Points the registry at the cockpit's live pane ids; called once as the cockpit view model is built.</summary>
    public void SetSource(Func<IReadOnlySet<string>> source) => _source = source;
}
