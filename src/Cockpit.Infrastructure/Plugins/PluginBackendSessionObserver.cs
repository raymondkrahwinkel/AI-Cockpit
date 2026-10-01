using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Plugins;

// AC-1392: `ICockpitHost.Sessions` for a backend, on ISessionRegistry: the open sessions and which one closed. It has
// no selection (Active* stay empty, D6), so no output is ever from the active session. AC-1415: output, tool activity
// and turn images come from each live handle, followed the way `SessionEventsBridge._Follow` follows its rows.
public sealed class PluginBackendSessionObserver : ICockpitSessionObserver
{
    private readonly ISessionRegistry _registry;
    private readonly Lock _gate = new();
    private HashSet<string> _live = [];
    private readonly Dictionary<ISessionHandle, (Action<string> OnText, Action<SessionToolCall> OnToolCall)> _followed = new(ReferenceEqualityComparer.Instance);

    // Subscribed before the first read, so no change after that read goes unseen.
    public PluginBackendSessionObserver(ISessionRegistry registry)
    {
        _registry = registry;
        _registry.Changed += _OnRegistryChanged;
        lock (_gate)
        {
            _live = _LivePaneIds();
            _Follow();
        }
    }

    public IReadOnlyList<OpenCockpitSession> OpenSessions =>
        [.. _registry.All.Select(session => new OpenCockpitSession(session.PaneId, session.Title)),
            .. _registry.Assistant is { } assistant
                ? [new OpenCockpitSession(assistant.PaneId, assistant.Title)]
                : Array.Empty<OpenCockpitSession>()];

    public event EventHandler<SessionOutputText>? OutputProduced;

    public event EventHandler<SessionToolActivity>? ToolActivityObserved;

    public event EventHandler<string>? SessionClosed;

    public IReadOnlyList<SessionImageAttachment> GetCurrentTurnImages(string paneId) =>
        _Find(paneId) is { } handle ? SessionImageAttachments.From(handle.CurrentTurnImages) : [];

    // AC-1397: the same panes OpenSessions lists, the assistant included, read from the registry's own snapshot.
    public string? GetWorkingDirectory(string paneId) => _Find(paneId)?.WorkingDirectory;

    private ISessionHandle? _Find(string paneId) =>
        _registry.Find(paneId) ?? (_registry.Assistant is { } assistant && string.Equals(assistant.PaneId, paneId, StringComparison.Ordinal) ? assistant : null);

    // Changed says only that something moved; the panes that were live before and are not now are the ones that
    // closed. Read and swapped under the gate, so two changes on two threads are compared in the order they read.
    private void _OnRegistryChanged(object? sender, EventArgs e)
    {
        List<string> closed;
        lock (_gate)
        {
            var now = _LivePaneIds();
            closed = [.. _live.Where(paneId => !now.Contains(paneId))];
            _live = now;
            _Follow();
        }

        foreach (var paneId in closed)
        {
            SessionClosed?.Invoke(this, paneId);
        }
    }

    // Keyed by handle, as the bridge does: a pane id registered again is a new handle, and the old one must not keep
    // raising under it. A handle raises outside its own lock, so a plugin's handler never runs under it.
    private void _Follow()
    {
        var live = _registry.All.Append(_registry.Assistant).OfType<ISessionHandle>().ToHashSet<ISessionHandle>(ReferenceEqualityComparer.Instance);
        foreach (var gone in _followed.Keys.Where(handle => !live.Contains(handle)).ToList())
        {
            gone.OutputTextProduced -= _followed[gone].OnText;
            gone.ToolActivityProduced -= _followed[gone].OnToolCall;
            _followed.Remove(gone);
        }

        foreach (var handle in live.Where(handle => !_followed.ContainsKey(handle)))
        {
            Action<string> onText = text => OutputProduced?.Invoke(this, new SessionOutputText(text, handle.WorkingDirectory, IsFromActiveSession: false));
            Action<SessionToolCall> onToolCall = call =>
                ToolActivityObserved?.Invoke(this, new SessionToolActivity(call.PaneId, call.ToolName, call.InputJson, call.ResultContent, call.IsError));
            handle.OutputTextProduced += onText;
            handle.ToolActivityProduced += onToolCall;
            _followed[handle] = (onText, onToolCall);
        }
    }

    // The assistant too, which `All` leaves out, so the pane OpenSessions lists is also the one reported closed.
    private HashSet<string> _LivePaneIds() =>
        new([.. _registry.All.Select(session => session.PaneId), .. _registry.Assistant is { } assistant ? [assistant.PaneId] : Array.Empty<string>()], StringComparer.Ordinal);
}
