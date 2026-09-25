using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Diagram.Collab;

// The session a surface is coupled to, and the "are they still there" state around it (AC-870). The registry's own
// Couple stays the caller's: IDiagramAccessRegistry.Couple and IWhiteboardAccessRegistry.Couple are unrelated types
// with the same shape, not a common interface.
//
// F2.13/AC-1401: ICockpitUiHost carries no BindToSession/Sessions (those stay backend-only), so this reads a bound
// pane's liveness and name over DiagramChannelContract.SessionBind/SessionList instead of ICockpitHost.BindToSession — the
// same "reach the backend through the channel, not host.Services" move AC-1400 already made for the registries.
internal sealed class SurfaceSessionBinding
{
    private readonly ICockpitUiHost _host;
    private readonly IPluginUiChannel? _channel;
    private readonly Action _onChanged;
    private readonly IDisposable? _closed;
    private _BindResult _bound;

    // AC-1400: that the session ended arrives as the backend's session.closed event on the plugin's channel. No
    // channel (an older host) means the end is never announced, the same as a surface with no registry.
    public SurfaceSessionBinding(ICockpitUiHost host, IPluginUiChannel? channel, string? initialPaneId, Action onChanged)
    {
        _host = host;
        _channel = channel;
        _onChanged = onChanged;
        _bound = _Bind(initialPaneId);
        _closed = channel?.Subscribe(DiagramChannelContract.SessionClosed, channelEvent => _OnClosed(DiagramChannelContract.ReadString(channelEvent.Payload, 0)));
    }

    public bool IsLive => _bound.IsLive;

    // Stays readable after the session ends, same as the bound pane id itself.
    public string PaneId => _bound.PaneId;

    public string? LivePaneId => _bound.IsLive ? _bound.PaneId : null;

    public string? DisplayName => _bound.SessionName ?? BoundSessionName;

    // The name is read here and kept, not read on demand: by the time the session ends it is gone from the
    // cockpit, and "session … has ended" with no name in it is the one moment the operator needs one.
    public string? BoundSessionName { get; private set; }

    public string? EndedSessionName { get; private set; }

    public Task SendAsync(string text) => _host.SendToSessionAsync(_bound.PaneId, text);

    // No channel (an older host) means no registry either, the same "nothing to ask" state SurfaceWindowOpener's
    // callers already draw — a fresh bind then reads as "no session", never an exception.
    private _BindResult _Bind(string? paneId)
    {
        var pane = paneId ?? "";
        var bound = _channel is null
            ? new _BindResult(pane, null, false)
            : _channel.InvokeAsync(DiagramChannelContract.SessionBind, JsonSerializer.SerializeToElement(new object?[] { pane }, DiagramChannelContract.Json))
                .GetAwaiter().GetResult()
                .Deserialize<_BindResult>(DiagramChannelContract.Json) ?? new _BindResult(pane, null, false);
        BoundSessionName = bound.SessionName ?? (bound.IsLive ? bound.PaneId : null);
        return bound;
    }

    // The session behind this surface ended. Nothing here closes the surface's window, and nothing here drops the
    // coupling either — the host releases it and the registry's own CouplingChanged brings that back. This only
    // supplies the name that is gone by then. Compared on the UI thread, where Recouple swaps the binding.
    private void _OnClosed(string paneId) => Dispatcher.UIThread.Post(() =>
    {
        if (paneId != _bound.PaneId)
        {
            return;
        }

        EndedSessionName = BoundSessionName;
        _onChanged();
    });

    // Couples to another running session — the way out of "surface open, no agent", after the bound session ended
    // or the operator disconnected. Returns the refusal message when `couple` throws (the surface is already
    // coupled to a different agent); null once it landed and this binding points at the new session.
    public string? Recouple(string paneId, Action<string> couple)
    {
        try
        {
            couple(paneId);
        }
        catch (InvalidOperationException exception)
        {
            return exception.Message;
        }

        _bound = _Bind(paneId);
        EndedSessionName = null;
        _onChanged();
        return null;
    }

    public void Dispose() => _closed?.Dispose();

    // The open sessions by name (AC-833), so recoupling names a session instead of guessing one. No running
    // session is a state worth reading, not an empty menu.
    public void ShowSessionPicker(Control anchor, Action<string> recouple)
    {
        var open = _channel is null
            ? []
            : _channel.InvokeAsync(DiagramChannelContract.SessionList, default).GetAwaiter().GetResult().Deserialize<List<OpenCockpitSession>>(DiagramChannelContract.Json) ?? [];
        var flyout = new MenuFlyout();
        if (open.Count == 0)
        {
            flyout.Items.Add(new MenuItem { Header = "No open sessions", IsEnabled = false });
        }

        foreach (var session in open)
        {
            var item = new MenuItem { Header = session.Name };
            item.Click += (_, _) => recouple(session.PaneId);
            flyout.Items.Add(item);
        }

        flyout.ShowAt(anchor);
    }

    // Mirrors DiagramChannelContract.SessionBind's answer shape by property name — the UI part carries no reference to the
    // backend assembly to share the record itself (F2.13/AC-1401's whole point).
    private sealed record _BindResult(string PaneId, string? SessionName, bool IsLive);
}
