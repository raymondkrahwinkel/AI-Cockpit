using Material.Icons;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugin.ExampleWorkspace;

/// <summary>
/// The example workspace plugin (AC-122): its only contribution is one full-surface workspace type. Where a
/// widget fills one cell of a Dashboard, this owns its whole body — and embeds a real host session in it — so it
/// is what proves the workspace-type SDK end to end from outside the host, the way the clock proves the widget
/// SDK. Bundled so the example is there out of the box.
/// </summary>
public sealed class ExampleWorkspacePlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "example-workspace",
        DisplayName: "Example Workspace",
        // Kept in lockstep with plugin.json's "version": the manifest gates loading, this shows in the plugin list.
        Version: "0.1.0",
        Author: "Cockpit",
        Description: "A full-surface workspace a plugin draws end to end, with a real host session embedded in it.");

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        // The type id is persisted with every workspace of this type, so it is an API surface — changing it would
        // orphan desks people have already created. The plugin owns the whole body; the host draws only the tab.
        host.AddWorkspaceType(new WorkspaceTypeRegistration("workspace.example", "Example", context => new ExampleWorkspaceBody(context))
        {
            IconKind = MaterialIconKind.ViewGridPlusOutline,
            Description = "A workspace a plugin draws end to end, with a live session embedded in it.",
        });
    }

    public void Dispose()
    {
    }
}
