using Avalonia.Automation.Peers;
using Avalonia.Controls;

namespace Cockpit.App.Controls;

// AC-1013: Cockpit deliberately serves no UI-Automation tree below its windows — an external UIA client
// realises a COM node per control that Avalonia never releases on detach (#8240), pinning a closed session
// pane's transcript until it disconnects. Details: dropped the in-app-voice-assistant rationale and the IRootProvider note.
internal sealed class NoChildrenWindowPeer : WindowAutomationPeer
{
    public NoChildrenWindowPeer(Window owner) : base(owner)
    {
    }

    protected override IReadOnlyList<AutomationPeer>? GetChildrenCore() => Array.Empty<AutomationPeer>();
}
