using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Mcp;
using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Mcp;

namespace Cockpit.App.Plugins;

// The effective MCP-server set a session sees (#26, AC-11): the user-managed registry merged with what each
// active plugin provides for itself. The providers are the plugins that registered themselves as
// `IPluginMcpProvider` in their `ConfigureServices`, injected here as a set. This is what the
// fan-out and the New-session checklist read, so plugin-owned servers are offered and per-session uncheckable
// alongside registry ones — while the MCP-servers manager keeps reading `IMcpServerStore` directly
// and so never lists them.
internal sealed class McpServerCatalog(
    IMcpServerStore store,
    IProjectStore projectStore,
    IEnumerable<IPluginMcpProvider> pluginProviders,
    IEnumerable<ICockpitInternalMcpProvider> internalProviders,
    ILogger<McpServerCatalog> logger)
    : IMcpServerCatalog, ISingletonService
{
    public async Task<IReadOnlyList<McpServerConfig>> GetServersForProjectAsync(string? projectId, CancellationToken cancellationToken = default)
    {
        var registry = await store.LoadAsync(cancellationToken).ConfigureAwait(false);

        // The cockpit's own loopback endpoints (AC-40): answered live, never in the store, so the manager never
        // lists them while the session fan-out still sees them.
        var internalServers = internalProviders.SelectMany(_ServersOf).ToList();

        // Loaded once, ahead of both places this session's project matters below: which Memory-role scheme(s) it
        // carries (AC-504) and, further down, its McpOverlay. Null when there is no projectId or nothing in the
        // store answers to it — the same "no project" case the overlay step always treated as a no-op.
        var projects = await projectStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        Project? project = string.IsNullOrEmpty(projectId) ? null : projects.Find(projectId);

        // AC-504: a project's own Memory-role rows, reduced to the scheme each one names — "depot.wispslate" out of
        // a stored "depot.wispslate:my-slug" — parsed here (Cockpit.Core, which owns ProjectMemoryRef) so a plugin
        // never has to parse a reference itself across the plugin-ALC boundary. A row whose reference does not
        // parse (a Folder row's plain path, say) contributes no scheme, which is exactly how a project with no
        // matching connection ends up with an empty list rather than a null-reference-shaped one.
        // Trimmed before parsing — the same rule SessionStartDefaults applies to a stored reference before parsing
        // it (Project.Resources.Reference is saved trimmed by the project editor, but a hand-edited cockpit.json is
        // not guaranteed to be) — so a reference with surrounding whitespace resolves to the same scheme here as it
        // does in a session's own standing instructions, rather than silently matching in one place and not the
        // other for the very same stored value.
        //
        // AC-766: an unscoped query (no project) takes the *union* of every project's schemes instead of one
        // project's own — "a project narrows what is selected, never what is offered" (ProjectMcpOverlay) applies to
        // a plugin's own bevraging too, not only the overlay step below, so a plugin-provided server (Depot, say)
        // reaches the project-agnostic catalog rather than only sessions on a project that names its scheme.
        var projectMemorySchemes = (project is not null ? (IReadOnlyList<Project>)[project] : projects.Projects)
            .SelectMany(candidate => candidate.Resources)
            .Where(resource => resource.Role == ProjectResourceRole.Memory && resource.ReachesSessions)
            .Select(resource => ProjectMemoryRef.TryParse(resource.Reference.Trim(), out var scheme, out _) ? scheme : null)
            .Where(scheme => scheme is not null)
            .Select(scheme => scheme!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // AC-500/AC-504: projectId (and now the project's own memory schemes) reach each plugin's own bevraging —
        // not just the overlay step below — so a plugin can contribute a server that exists only for one project
        // (its own Depot connection, say) rather than every project seeing every plugin server and the overlay
        // only ever being able to remove one.
        // AC-736: which servers a plugin contributes only because this project names a memory scheme, asked by taking
        // the same providers' answer without those schemes — only a plugin knows how its own schemes map back to its
        // own servers, and asking rather than deriving keeps this true for any such plugin, not Depot alone.
        // AC-766: an unscoped query has no single project to link a server to, whatever schemes reached the
        // providers above — so it never marks one ProjectLinked, unconditionally, rather than through `schemeless`
        // happening to hold every name the union of schemes produced.
        var schemeless = project is null || projectMemorySchemes.Count == 0
            ? null
            : pluginProviders
                .SelectMany(provider => _ServersOf(provider, projectId, []))
                .Select(contribution => contribution.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var pluginServers = pluginProviders
            .SelectMany(provider => _ServersOf(provider, projectId, projectMemorySchemes))
            .Select(PluginMcpMapping.ToServerConfig)
            .Select(server => schemeless?.Contains(server.Name) == false ? server with { ProjectLinked = true } : server)
            .ToList();

        var servers = Merge(registry, [.. internalServers, .. pluginServers]);
        return project?.McpOverlay.ApplyTo(servers) ?? servers;
    }

    public Task<IReadOnlyList<McpServerConfig>> GetServersAsync(CancellationToken cancellationToken = default) =>
        GetServersForProjectAsync(projectId: null, cancellationToken);

    // The registry with the cockpit-hosted and plugin-owned servers merged in: registry entries first, then the
    // provided ones. A provider owns its own names, so its live answer wins over a registry entry of the same name
    // — the case that arises for one start after upgrade, before the older push entries are reconciled away. Two
    // providers claiming the same name is not expected (the cockpit's own endpoint names are disjoint from the
    // plugins'), but if it ever happens the first one caller order gives — a cockpit-hosted endpoint ahead of a
    // plugin's — wins, rather than a session seeing the same server twice. Pulled out so the merge is unit-testable
    // without standing up a PluginManager.
    internal static IReadOnlyList<McpServerConfig> Merge(IReadOnlyList<McpServerConfig> registry, IReadOnlyList<McpServerConfig> providedServers)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var provided = providedServers.Where(server => seen.Add(server.Name)).ToList();
        return [.. registry.Where(server => !seen.Contains(server.Name)), .. provided];
    }

    // A plugin that throws while listing its servers must not break session start for everyone else — its servers
    // are simply absent for this assembly, and the failure is logged. projectId flows through unconditionally
    // (AC-500) — null for a session with no project is exactly the value IPluginMcpProvider's default overload
    // reads as "give me the project-agnostic set", so an unscoped caller sees the same servers as before.
    private IReadOnlyList<McpServerContribution> _ServersOf(IPluginMcpProvider provider, string? projectId, IReadOnlyList<string> projectMemorySchemes)
    {
        try
        {
            return provider.GetMcpServers(projectId, projectMemorySchemes);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "A plugin failed to list its MCP servers; leaving them out of this session.");
            return [];
        }
    }

    private IReadOnlyList<McpServerConfig> _ServersOf(ICockpitInternalMcpProvider provider)
    {
        try
        {
            return provider.GetServers();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "A cockpit-hosted MCP source failed to list its servers; leaving them out of this session.");
            return [];
        }
    }
}
