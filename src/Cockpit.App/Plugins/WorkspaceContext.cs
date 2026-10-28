using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.App.Plugins;

// The host's `IWorkspaceContext`: what one plugin workspace is handed, built per workspace so its
// storage and its refresh signal are its own. Embedding a session is routed to the shell's
// `IEmbeddedSessionHost`, which owns the session's lifetime; the body only places the view.
internal sealed class WorkspaceContext(
    string workspaceId,
    IPluginStorage pluginStorage,
    ICockpitSessionObserver sessions,
    IEmbeddedSessionHost? embeddedSessions) : IWorkspaceContext
{
    public string WorkspaceId => workspaceId;

    public IPluginStorage Storage { get; } = new WorkspaceStorage(pluginStorage, workspaceId);

    public ICockpitSessionObserver Sessions => sessions;

    public string? InstanceKey { get; internal set; }

    public bool IsPreview { get; private set; }

    // AC-1750: wired by WorkspacesViewModel, which owns the tab these act on.
    internal Action<string>? TitleSet { get; set; }

    internal Action<WorkspaceMarker?>? MarkerSet { get; set; }

    internal Action? PinRequested { get; set; }

    public event EventHandler? Pinned;

    public event EventHandler? RefreshRequested;

    public event EventHandler? Closed;
    private Func<Task<bool>>? _closeGuard;

    public void SetCloseGuard(Func<Task<bool>>? guard)
    {
        _closeGuard = guard;
        CloseGuardVersion++;
    }
    internal long CloseGuardVersion { get; private set; }

    internal bool HasCloseGuard => _closeGuard is not null;

    internal Task<bool> ConfirmCloseAsync() => _closeGuard?.Invoke() ?? Task.FromResult(true);

    public IEmbeddedSession EmbedSession(EmbeddedSessionRequest request)
    {
        // Null only in the design-time/test graph, where there is no session machinery behind the shell. A real
        // host always supplies one, so a body that reaches for a session there is asking for something the host
        // cannot give — a clear throw beats a placeholder that silently shows nothing.
        if (embeddedSessions is null)
        {
            throw new InvalidOperationException("This host cannot embed sessions.");
        }

        return embeddedSessions.Embed(workspaceId, request);
    }

    // Asks this workspace to refresh — raised by the host (a workspace-wide refresh). Host-side only: a body
    // listens to `RefreshRequested`, it does not fire it.
    public void RequestRefresh() => RefreshRequested?.Invoke(this, EventArgs.Empty);

    public void SetTitle(string title) => TitleSet?.Invoke(title);

    public void SetMarker(WorkspaceMarker? marker) => MarkerSet?.Invoke(marker);

    public void Pin()
    {
        if (IsPreview)
        {
            PinRequested?.Invoke();
        }
    }

    // Host-side: seeds the preview state, and raises `Pinned` when a preview is pinned (by `Pin` or a double-click on the tab).
    internal void SetPreview(bool isPreview)
    {
        var wasPreview = IsPreview;
        IsPreview = isPreview;
        if (wasPreview && !isPreview)
        {
            Pinned?.Invoke(this, EventArgs.Empty);
        }
    }

    // Raised by the shell when this workspace is really closed (its tab dismissed), so a body can tear down a long-running job.
    public void RaiseClosed()
    {
        _closeGuard = null;
        Closed?.Invoke(this, EventArgs.Empty);
    }
}
