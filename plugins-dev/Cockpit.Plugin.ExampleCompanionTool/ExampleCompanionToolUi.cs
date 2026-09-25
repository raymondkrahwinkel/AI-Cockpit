using Material.Icons;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.CompanionTools;

namespace Cockpit.Plugin.ExampleCompanionTool;

// The example companion-tool plugin (AC-240; AC-1395, pure UI per F2.1): its only contribution is one mini-tool
// in the companion window — proof that a plugin, not just the host's own first-party tools (AC-238's assistant
// indicator), can reach that extension point end to end. Bundled so the example is there out of the box.
public sealed class ExampleCompanionToolUi : ICockpitPluginUi
{
    public void InitializeUi(ICockpitUiHost host)
    {
        // The tool id is persisted as the key its own storage lives under, so it is an API surface — changing it
        // would orphan the click count anyone already has.
        host.AddCompanionTool(new CompanionToolRegistration(
            "example-companion-tool.hello", "Example", context => new ExampleCompanionToolView(context))
        {
            IconKind = MaterialIconKind.HandWave,
            Tooltip = "Example companion tool",
        });
    }
}
