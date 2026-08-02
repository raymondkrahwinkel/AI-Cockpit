using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Projects;
using Cockpit.Plugin.Depot.Settings;
using Cockpit.Plugin.Depot.Ui;

namespace Cockpit.Plugin.Depot;

// Depot as a project memory source (AC-165/166) plus, since AC-243, a settings view where an operator connects one
// or more Depot instances. Since AC-501, each connection also registers its own memory source (see
// `DepotMemorySource`) rather than one fixed registration shared by every instance. Since AC-504, each
// connection's MCP server (an OAuth `McpServerContribution` — Depot has a single auth path, so the
// plugin never holds a credential of its own; the host drives the sign-in and keeps the token) is offered
// per-project (`GetMcpServers(string?, IReadOnlyList{string})`) rather than pushed into the shared
// registry for every session to see: a session on a project whose memory lives in one connection is offered that
// connection's server, not every configured instance. Reading and writing project memory still happens through the
// Depot MCP inside the session itself.
public sealed class DepotPlugin : ICockpitPlugin, IPluginMcpProvider
{
    // Kept from Initialize so GetMcpServers can read the current connection list each time the host asks, rather
    // than a snapshot taken once — the same reason YouTrackPlugin keeps its own settings reference.
    private DepotSettings? _settings;

    // Kept from Initialize (AC-503) so GetMcpServers(string?, IReadOnlyList<string>) below can hand
    // BuildRegistrationPairs the host it needs to wire CheckReachability onto every registration it builds — even
    // though this particular call site only reads Registration.Scheme back out, BuildRegistrationPairs itself always
    // needs a host to build a registration at all.
    private ICockpitHost? _host;

    public PluginMetadata Metadata { get; } = new(
        Id: "depot",
        DisplayName: "Depot",
        Author: "Cockpit",
        Description: "Lets a project's memory live in a Depot project instead of a folder, and connects one or more Depot instances so a session can reach them.");

    public void ConfigureServices(IServiceCollection services)
    {
        // Register this plugin as the source of its own MCP servers (AC-504): the host's McpServerCatalog injects
        // every IPluginMcpProvider and asks each when it assembles a session, rather than this plugin pushing its
        // servers into the shared registry. Same instance the host initializes, so GetMcpServers reads the settings
        // this plugin later loads in Initialize.
        services.AddSingleton<IPluginMcpProvider>(this);
    }

    public void Initialize(ICockpitHost host)
    {
        var settings = new DepotSettings(host.Storage);
        _settings = settings;
        _host = host;
        host.AddSettings(() => new DepotSettingsControl(host, settings));

        // AC-499: declared unconditionally, even with zero connections — this is the doorless-dead-end bug itself:
        // zero connections meant no "Depot" option anywhere in the project editor's picker and no way to reach this
        // plugin's settings from it. The family is what lets the picker say "Depot" (and offer ConfigureAsync as
        // the way to add a first connection) regardless of how many are configured right now.
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

        // AC-504: session delivery no longer pushes this plugin's servers into the shared registry — the host asks
        // for them through GetMcpServers when a session is assembled. Reclaim what an earlier version (AC-243)
        // pushed, the same move YouTrackPlugin made when it left the push path (AC-11), so those entries leave the
        // MCP-servers manager and this plugin is their sole owner from here on. Sequential, not one fire-and-forget
        // call per connection: RemoveMcpServer does its own load-modify-save round trip against the shared store
        // with no locking across calls, so firing several at once for two different connections would race — each
        // reads the same stale snapshot and the last SaveAsync to finish silently keeps whichever connection lost
        // the race, exactly the leak this reclaim exists to close.
        _ = _ReclaimPushedMcpServersSequentiallyAsync(host, settings.Connections);
    }

    private static async Task _ReclaimPushedMcpServersSequentiallyAsync(ICockpitHost host, IReadOnlyList<Model.DepotConnectionRegistration> connections)
    {
        foreach (var connection in connections)
        {
            await host.RemoveMcpServer(connection.McpServerName).ConfigureAwait(false);
        }
    }

    // Every connection this plugin has configured (AC-504), unscoped by project. Session delivery itself never
    // reaches this overload — the catalog always calls `GetMcpServers(string?, IReadOnlyList{string})`,
    // which this plugin overrides directly — but the host falls back to it when resolving an OAuth sign-in for a
    // name the shared registry no longer carries: signing in happens from this plugin's own settings view, which
    // has no project of its own to scope a call to the overload below by.
    public IReadOnlyList<McpServerContribution> GetMcpServers() =>
        _settings is null ? [] : _settings.Connections.Select(_ContributionFor).ToList();

    // The connection whose own memory-source scheme is among `projectMemorySchemes` (AC-504) —
    // zero, one, or (a project with more than one Memory row pointing at Depot) several. `projectId`
    // itself is unused: the schemes already say everything this plugin needs to know about which of its own
    // connections the calling project actually points at.
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

    // AC-499: connection.Url is already the normalized base by the time it gets here — DepotConnectionRowControl
    // .ToRegistration normalizes on save, and DepotSettings migrates any value stored before this fix existed,
    // exactly once, on load. This only appends /mcp; it must not call DepotUrlNormalizer.Normalize again, because
    // that call is no longer safe to repeat (see DepotUrlNormalizer's own doc comment) — a base whose deployment
    // path genuinely ends in /mcp would lose that segment on a second pass.
    private static McpServerContribution _ContributionFor(Model.DepotConnectionRegistration connection)
    {
        return new(Name: connection.McpServerName, Url: $"{connection.Url}/mcp")
        {
            // AC-403: this connection's own id, so the host files its OAuth token under something the operator
            // cannot edit. The name is "Depot: {Name}" and that Name is a free-text field in the settings view —
            // renaming a connection used to leave its sign-in stranded under the old name, and two connections to
            // the same host that swapped names could each end up presenting the other's bearer. The id is minted
            // once per row and stored with it, so neither can happen.
            Id = connection.Id,
            // Scheme+host+port of the stored base URL, not its own path — Depot's protected-resource metadata
            // names the origin as its authorization_servers entry, not a subpath. Falls back to the base URL
            // itself on the (defensive-only; the row's own validation already requires an absolute http(s) URL
            // before Sign-in is offered) chance it is not a parseable absolute URL.
            OAuthAuthority = DepotUrlNormalizer.Origin(connection.Url) ?? connection.Url,
        };
    }

    public void Dispose()
    {
        // Nothing to release: no timers, no clients, no subscriptions.
    }
}
