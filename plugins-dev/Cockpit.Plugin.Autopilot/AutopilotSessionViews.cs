using Avalonia.Controls;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Autopilot;

// AC-1398: the workspace places a session the backend holds by pane id. A view it placed stays placeable after the
// host lets its session go, as the Control the run used to hold did; a pane never seen shows nothing, traced once.
internal sealed class AutopilotSessionViews(ICockpitUiHost host, Action<string> trace)
{
    private readonly Dictionary<string, Control> _placed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unknownPanes = new(StringComparer.Ordinal);

    public Control? For(string? paneId)
    {
        if (paneId is null)
        {
            return null;
        }

        if (host.CreateEmbeddedSessionView(paneId) is { } view)
        {
            _placed[paneId] = view;
            return view;
        }

        if (_placed.TryGetValue(paneId, out var placed))
        {
            return placed;
        }

        if (_unknownPanes.Add(paneId))
        {
            trace($"Autopilot: no embedded session view for pane '{paneId}'; nothing is shown for it.");
        }

        return null;
    }

    // Drops the views of panes no active run names any more, so a settled run's sessions are not kept alive here.
    public void Retain(IEnumerable<string?> paneIds)
    {
        var live = paneIds.OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var paneId in _placed.Keys.Where(paneId => !live.Contains(paneId)).ToList())
        {
            _placed.Remove(paneId);
        }
    }
}
