using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Depot;

/// <summary>
/// Depot as a project memory source (AC-165/166). This plugin has no settings, no side-menu entry and no
/// dialog — its whole job is telling the host, once, that a project's <c>MemoryRef</c> of the shape
/// <c>depot:&lt;slug&gt;</c> names a Depot project and how a session should reach it
/// (see <see cref="DepotMemorySource"/>). Reading and writing that memory happens through the Depot MCP inside the
/// session itself; this plugin does not mount one.
/// </summary>
public sealed class DepotPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "depot",
        DisplayName: "Depot",
        Author: "Cockpit",
        Description: "Lets a project's memory live in a Depot project instead of a folder.");

    public void ConfigureServices(IServiceCollection services)
    {
        // No services of its own — the registration below needs nothing built.
    }

    public void Initialize(ICockpitHost host)
    {
        host.AddProjectMemorySource(DepotMemorySource.Registration);
    }

    public void Dispose()
    {
        // Nothing to release: no timers, no clients, no subscriptions.
    }
}
