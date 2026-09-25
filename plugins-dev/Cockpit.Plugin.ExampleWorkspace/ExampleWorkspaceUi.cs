using Material.Icons;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugin.ExampleWorkspace;

// The example workspace plugin (AC-122; AC-1395, pure UI per F2.1): one full-surface workspace type, proving
// the workspace-type SDK end to end the way the clock proves the widget SDK. Bundled out of the box.
public sealed class ExampleWorkspaceUi : ICockpitPluginUi
{
    public void InitializeUi(ICockpitUiHost host)
    {
        // The type id is persisted with every workspace of this type, so it is an API surface — changing it would
        // orphan desks people have already created. The plugin owns the whole body; the host draws only the tab.
        host.AddWorkspaceType(new WorkspaceTypeRegistration("workspace.example", "Example", context => new ExampleWorkspaceBody(context))
        {
            IconKind = MaterialIconKind.ViewGridPlusOutline,
            Description = "A workspace a plugin draws end to end, with a live session embedded in it.",
        });
    }
}
