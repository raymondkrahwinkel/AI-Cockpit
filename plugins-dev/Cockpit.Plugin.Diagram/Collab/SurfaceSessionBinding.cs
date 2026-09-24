using Avalonia.Controls;
using Avalonia.Threading;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Diagram.Collab;

// The session a surface is coupled to, and the "are they still there" state around it (AC-870). The registry's own
// Couple stays the caller's: IDiagramAccessRegistry.Couple and IWhiteboardAccessRegistry.Couple are unrelated types
// with the same shape, not a common interface.
internal sealed class SurfaceSessionBinding
{
    private readonly ICockpitHost _host;
    private readonly Action _onChanged;
    private readonly IDisposable? _closed;
    private IPluginSessionBinding _binding;

    // AC-1400: that the session ended arrives as the backend's session.closed event on the plugin's channel. No
    // channel (an older host) means the end is never announced, the same as a surface with no registry.
    public SurfaceSessionBinding(ICockpitHost host, IPluginUiChannel? channel, string? initialPaneId, Action onChanged)
    {
        _host = host;
        _onChanged = onChanged;
        _binding = _Bind(initialPaneId);
        _closed = channel?.Subscribe(DiagramChannel.SessionClosed, channelEvent => _OnClosed(DiagramChannel.ReadString(channelEvent.Payload, 0)));
    }

    public bool IsLive => _binding.IsLive;

    // Stays readable after the session ends, same as IPluginSessionBinding.PaneId itself.
    public string PaneId => _binding.PaneId;

    public string? LivePaneId => _binding.IsLive ? _binding.PaneId : null;

    public string? DisplayName => _binding.SessionName ?? BoundSessionName;

    // The name is read here and kept, not read on demand: by the time the session ends it is gone from the
    // cockpit, and "session … has ended" with no name in it is the one moment the operator needs one.
    public string? BoundSessionName { get; private set; }

    public string? EndedSessionName { get; private set; }

    public Task SendAsync(string text) => _binding.SendAsync(text);

    private IPluginSessionBinding _Bind(string? paneId)
    {
        var binding = _host.BindToSession(paneId ?? "");
        BoundSessionName = binding.SessionName ?? (binding.IsLive ? binding.PaneId : null);
        return binding;
    }

    // The session behind this surface ended. Nothing here closes the surface's window, and nothing here drops the
    // coupling either — the host releases it and the registry's own CouplingChanged brings that back. This only
    // supplies the name that is gone by then. Compared on the UI thread, where Recouple swaps the binding.
    private void _OnClosed(string paneId) => Dispatcher.UIThread.Post(() =>
    {
        if (paneId != _binding.PaneId)
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

        _binding.Dispose();
        _binding = _Bind(paneId);
        EndedSessionName = null;
        _onChanged();
        return null;
    }

    public void Dispose()
    {
        _closed?.Dispose();
        _binding.Dispose();
    }

    // The open sessions by name (AC-833), so recoupling names a session instead of guessing one. No running
    // session is a state worth reading, not an empty menu.
    public void ShowSessionPicker(Control anchor, Action<string> recouple)
    {
        var open = _host.Sessions.OpenSessions;
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
}
