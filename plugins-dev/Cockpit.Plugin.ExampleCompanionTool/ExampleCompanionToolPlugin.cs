using Material.Icons;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.CompanionTools;

namespace Cockpit.Plugin.ExampleCompanionTool;

// The example companion-tool plugin (AC-240): its only contribution is one mini-tool in the companion window —
// proof that a plugin, not just the host's own first-party tools (AC-238's assistant indicator), can reach that
// extension point end to end. Bundled so the example is there out of the box.
public sealed class ExampleCompanionToolPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "example-companion-tool",
        DisplayName: "Example Companion Tool",
        Author: "Cockpit",
        Description: "A mini-tool a plugin draws end to end in the companion window: an icon and a click action.");

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
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

    public void Dispose()
    {
    }
}
