using Avalonia.Controls;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Autopilot;

// AC-1398: the workspace places a session the backend holds by pane id. A pane the host has let go of — a step
// session closed after its step — shows nothing rather than an empty frame, and is traced once, not per render.
internal sealed class AutopilotSessionViews(ICockpitUiHost host, Action<string> trace)
{
    private readonly HashSet<string> _unknownPanes = new(StringComparer.Ordinal);

    public Control? For(string? paneId)
    {
        if (paneId is null)
        {
            return null;
        }

        if (host.CreateEmbeddedSessionView(paneId) is { } view)
        {
            return view;
        }

        if (_unknownPanes.Add(paneId))
        {
            trace($"Autopilot: no embedded session view for pane '{paneId}'; nothing is shown for it.");
        }

        return null;
    }
}
