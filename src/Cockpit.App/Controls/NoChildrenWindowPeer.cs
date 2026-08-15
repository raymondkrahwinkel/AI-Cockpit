using Avalonia.Automation.Peers;
using Avalonia.Controls;

namespace Cockpit.App.Controls;

// Cockpit serves NO UI-Automation tree below its windows. It has its own in-app voice assistant, so external UIA
// clients (screen readers, PowerToys, desktop-automation tools) are deliberately not exposed the window contents —
// and serving them is what leaks: an external client realises a COM node per control (AutomationNode, a CCW) that
// Avalonia never releases on detach (issue #8240), so every closed session pane's transcript stays pinned until the
// client disconnects. Reporting no children means the client can never descend into (and thus never pin) the pane
// contents. The window peer itself stays a real WindowAutomationPeer — RootAutomationNode requires an IRootProvider
// or WM_GETOBJECT throws — so only its children are hidden, not the root.
internal sealed class NoChildrenWindowPeer : WindowAutomationPeer
{
    public NoChildrenWindowPeer(Window owner) : base(owner)
    {
    }

    protected override IReadOnlyList<AutomationPeer>? GetChildrenCore() => Array.Empty<AutomationPeer>();
}
