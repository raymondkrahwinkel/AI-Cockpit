using Material.Icons;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugin.FanOut;

// Fan-out (AC-1395, pure UI per F2.1): one task started on several agents at once, tiled side by side. Its
// only contribution is a workspace type, which needs a window, so it has no backend part at all — the surface
// owns the whole body and embeds each arm's session in its own worktree, so the arms can be read against each other.
public sealed class FanOutUi : ICockpitPluginUi
{
    public void InitializeUi(ICockpitUiHost host)
    {
        // The type id is persisted with every workspace of this type, so it is an API surface — changing it would
        // orphan runs people have already set up.
        host.AddWorkspaceType(new WorkspaceTypeRegistration("workspace.fanout", "Fan-out", context => new FanOutWorkspaceBody(host, context))
        {
            IconKind = MaterialIconKind.CallSplit,
            Description = "One task, several agents on it at once, tiled side by side.",
        });
    }
}
