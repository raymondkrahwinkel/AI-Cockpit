using Material.Icons;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugin.FanOut;

/// <summary>
/// Fan-out: one task started on several agents at once, tiled side by side. Its only contribution is a
/// workspace type — the surface owns the whole body and embeds N host sessions in it, each isolated in its own
/// worktree, so the arms can be read against each other afterwards instead of having fought over one checkout.
/// The operator starts a run; nothing here spawns sessions on an agent's behalf.
/// </summary>
public sealed class FanOutPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "fan-out",
        DisplayName: "Fan-out",
        Author: "Cockpit",
        Description: "Run one task on several agents at once — each in its own worktree, tiled side by side.");

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        // The type id is persisted with every workspace of this type, so it is an API surface — changing it would
        // orphan runs people have already set up.
        host.AddWorkspaceType(new WorkspaceTypeRegistration("workspace.fanout", "Fan-out", context => new FanOutWorkspaceBody(host, context))
        {
            IconKind = MaterialIconKind.CallSplit,
            Description = "One task, several agents on it at once, tiled side by side.",
        });
    }

    public void Dispose()
    {
    }
}
