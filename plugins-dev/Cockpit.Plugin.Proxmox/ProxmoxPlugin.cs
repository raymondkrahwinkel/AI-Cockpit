using Material.Icons;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugin.Proxmox.Engine;
using Cockpit.Plugin.Proxmox.Mcp;
using Cockpit.Plugin.Proxmox.Security;
using Cockpit.Plugin.Proxmox.Settings;
using Cockpit.Plugin.Proxmox.Ui;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugin.Proxmox;

// Proxmox VE plugin entry point (AC-1038). Registers a Proxmox target and injects a cockpit-proxmox MCP server;
// the plugin talks to the REST API itself and keeps the token, gating every call through `ProxmoxAccessGate`.
// Sibling of the Docker and Kubernetes plugins.
public sealed class ProxmoxPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "proxmox",
        DisplayName: "Proxmox VE",
        Author: "Cockpit",
        Description: "Register a Proxmox VE host or cluster and give agents scoped, human-approved access to its nodes, VMs and LXC containers through a cockpit-proxmox MCP server. The plugin talks to the Proxmox REST API itself and keeps the API token — an agent never gets it. Connecting asks for consent once, and every change asks afresh with the literal action shown and is never remembered. Rollback and delete are off until you turn them on.");

    private ProxmoxEngine? _engine;

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        var settings = new ProxmoxSettings(host.Storage);
        var engine = new ProxmoxEngine(settings);
        _engine = engine;
        var gate = new ProxmoxAccessGate(host);
        var tools = new ProxmoxMcpTools(settings, gate, engine);

        host.AddSettings(() => new ProxmoxSettingsControl(host, settings));
        host.AddToolbarAction(new ToolbarAction("Proxmox settings", MaterialIconKind.Server, () => host.ShowSettingsAsync()));
        _ = host.AddMcpEndpoint("cockpit-proxmox", tools, isEnabled: () => settings.McpEnabled);

        // Read-only surface over the same gate/engine as the MCP tools — no second way to reach the API.
        host.AddWorkspaceType(new WorkspaceTypeRegistration("proxmox.overview", "Proxmox", context => new ProxmoxOverviewBody(context, gate, engine))
        {
            IconKind = MaterialIconKind.Server,
            Description = "Nodes, VMs, LXC containers and storage for a configured Proxmox target.",
        });

        // A settings save may have changed the target or its trusted certificate; drop the cached client so the
        // next call rebuilds it.
        host.OnSettingsSaved(engine.Invalidate);
    }

    public void Dispose() => _engine?.Dispose();
}
