using Material.Icons;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Projects;
using Cockpit.Plugin.Depot.Settings;
using Cockpit.Plugin.Depot.Ui;

namespace Cockpit.Plugin.Depot;

// Depot as a project memory source (AC-165/166) with a settings view for connecting one or more instances
// (AC-243); each connection registers its own memory source (AC-501, see `DepotMemorySource`). Since AC-504
// each connection's MCP server is offered per-project rather than pushed into the shared registry.
public sealed class DepotPlugin : ICockpitPlugin, IPluginMcpProvider
{
    // Kept from Initialize so GetMcpServers can read the current connection list each time the host asks, rather
    // than a snapshot taken once — the same reason YouTrackPlugin keeps its own settings reference.
    private DepotSettings? _settings;

    // Kept from Initialize (AC-503) so GetMcpServers below can hand BuildRegistrationPairs the host it needs
    // to wire CheckReachability, even though this call site only reads Registration.Scheme back out.
    private ICockpitHost? _host;

    public PluginMetadata Metadata { get; } = new(
        Id: "depot",
        DisplayName: "Depot",
        Author: "Cockpit",
        Description: "Lets a project's memory live in a Depot project instead of a folder, and connects one or more Depot instances so a session can reach them.");

    public void ConfigureServices(IServiceCollection services)
    {
        // Register this plugin as the source of its own MCP servers (AC-504): the host's McpServerCatalog
        // asks each IPluginMcpProvider when assembling a session, rather than this plugin pushing servers
        // into the shared registry. Same instance the host initializes, so GetMcpServers sees Initialize's settings.
        services.AddSingleton<IPluginMcpProvider>(this);
    }

    public void Initialize(ICockpitHost host)
    {
        var settings = new DepotSettings(host.Storage);
        _settings = settings;
        _host = host;
        host.AddSettings(() => new DepotSettingsControl(host, settings));
        // AC-784: same global-toolbar route the Kubernetes plugin uses (AC-91) — the project editor's own
        // "Servers…" button above still opens settings the same way it always has.
        host.AddToolbarAction(new ToolbarAction("Depot settings", MaterialIconKind.Database, () => host.ShowSettingsAsync()));

        // AC-499: declared unconditionally, even with zero connections — fixes the doorless-dead-end bug where
        // zero connections meant no "Depot" option anywhere and no way to reach this plugin's settings.
        host.AddProjectMemorySourceFamily(new ProjectMemorySourceFamily(DepotMemorySource.Scheme, "Depot")
        {
            EmptyHint = "No Depot server configured yet.",
            ConfigureAsync = _ => host.ShowSettingsAsync(),
        });

        // No connections configured yet means no memory source at all (AC-501) — the row behaves exactly as it did
        // before this plugin existed, rather than always offering a fixed "Depot project" nothing points at yet.
        foreach (var registration in DepotMemorySource.BuildRegistrations(settings.Connections, host))
        {
            host.AddProjectMemorySource(registration);
        }

        // AC-245: one shared-project source per connection, so the Projects workspace can list what this connection
        // shares beside the local projects. Same zero-connections-means-nothing rule as the memory sources above.
        foreach (var source in DepotMemorySource.BuildSharedProjectSources(settings.Connections, host))
        {
            host.AddSharedProjectSource(source);
        }

        // AC-504: session delivery now asks via GetMcpServers instead of the shared registry, so reclaim what
        // AC-243 pushed there. Sequential, not fire-and-forget: RemoveMcpServer's load-modify-save has no
        // locking across calls, so parallel calls would race on the same stale snapshot and drop a connection.
        _ = _ReclaimPushedMcpServersSequentiallyAsync(host, settings.Connections);
    }

    private static async Task _ReclaimPushedMcpServersSequentiallyAsync(ICockpitHost host, IReadOnlyList<Model.DepotConnectionRegistration> connections)
    {
        foreach (var connection in connections)
        {
            await host.RemoveMcpServer(connection.McpServerName).ConfigureAwait(false);
        }
    }

    // Every connection this plugin has configured (AC-504), unscoped by project. Session delivery never reaches
    // this overload — the catalog always calls the scoped overload below — but the host falls back to it for
    // OAuth sign-in from this plugin's own settings view, which has no project to scope a call by.
    public IReadOnlyList<McpServerContribution> GetMcpServers() =>
        _settings is null ? [] : _settings.Connections.Select(_ContributionFor).ToList();

    // The connection(s) whose memory-source scheme is among `projectMemorySchemes` (AC-504). `projectId`
    // is unused: the schemes already say which of this plugin's connections the project points at.
    public IReadOnlyList<McpServerContribution> GetMcpServers(string? projectId, IReadOnlyList<string> projectMemorySchemes)
    {
        if (_settings is null || _host is null || projectMemorySchemes.Count == 0)
        {
            return [];
        }

        var schemes = new HashSet<string>(projectMemorySchemes, StringComparer.OrdinalIgnoreCase);
        return DepotMemorySource.BuildRegistrationPairs(_settings.Connections, _host)
            .Where(pair => schemes.Contains(pair.Registration.Scheme))
            .Select(pair => _ContributionFor(pair.Connection))
            .ToList();
    }

    // AC-499: connection.Url is already the normalized base by the time it gets here — normalized on save and
    // migrated on load. This only appends /mcp; it must not call DepotUrlNormalizer.Normalize again, since
    // that is no longer safe to repeat (a base whose path genuinely ends in /mcp would lose that segment).
    private static McpServerContribution _ContributionFor(Model.DepotConnectionRegistration connection)
    {
        return new(Name: connection.McpServerName, Url: $"{connection.Url}/mcp")
        {
            // AC-403: this connection's own id, so the host files its OAuth token under something the operator
            // cannot edit — the Name is free-text, and renaming used to strand sign-in under the old name.
            Id = connection.Id,
            // Scheme+host+port of the stored base URL: Depot's protected-resource metadata names the origin as
            // its authorization_servers entry, not a subpath. Falls back to the base URL if unparseable (defensive only).
            OAuthAuthority = DepotUrlNormalizer.Origin(connection.Url) ?? connection.Url,
        };
    }

    public void Dispose()
    {
        // Nothing to release: no timers, no clients, no subscriptions.
    }
}
