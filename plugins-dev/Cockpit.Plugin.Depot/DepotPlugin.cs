using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugin.Depot.Settings;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Projects;

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

    private readonly List<IDisposable> _handlers = [];

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

        // AC-1394: the settings view and the global-toolbar shortcut to it moved to DepotUi.InitializeUi — this
        // channel action is what that view's own Save/Sign-in route now asks for instead of touching the
        // project-memory-source/shared-project-source/MCP registries directly.
        _handlers.Add(host.Channel.Handle(DepotChannel.SaveConnections, (payload, cancellationToken) =>
            _SaveConnectionsAsync(host, settings, payload, cancellationToken)));

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

    private static async Task _ReclaimPushedMcpServersSequentiallyAsync(ICockpitHost host, IReadOnlyList<DepotConnectionRegistration> connections)
    {
        foreach (var connection in connections)
        {
            await host.RemoveMcpServer(connection.McpServerName).ConfigureAwait(false);
        }
    }

    // AC-1394: the settings view's own Save/Sign-in route, moved here from Ui.DepotSettingsControl — that view
    // now only validates and asks for this; the write itself happens here, not there. `settings.Connections` is
    // read fresh (DepotSettings.Connections' own doc) before anything writes it, so `before` is genuinely the
    // pre-save state the diff below needs — writing it from the UI first, ahead of this call, would have this
    // read see its own "after" and turn the whole diff into a no-op.
    private static async Task<JsonElement> _SaveConnectionsAsync(
        ICockpitHost host, DepotSettings settings, JsonElement payload, CancellationToken cancellationToken)
    {
        var request = payload.Deserialize<DepotSaveConnectionsRequest>(DepotChannel.Json)
            ?? throw new ArgumentException("The request names no connections.", nameof(payload));

        var before = settings.Connections;
        var after = request.Connections.Select(connection => new DepotConnectionRegistration(connection.Id, connection.Name, connection.Url)).ToList();

        // The connection list itself is a plain storage write — the same slice the UI part's own Storage shares
        // (ICockpitUiHost's own remarks) — done here, before the sync below, so a row's Sign-in (which runs right
        // after this call returns) finds its connection already in storage under its registered MCP server name.
        settings.Connections = after;

        var keptNames = after.Select(connection => connection.McpServerName).ToHashSet(StringComparer.Ordinal);
        var orphanedNames = before
            .Select(connection => connection.McpServerName)
            .Where(name => !keptNames.Contains(name))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        _SyncMemorySources(host, before, after);
        _SyncSharedProjectSources(host, before, after);

        foreach (var orphanedName in orphanedNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await host.RemoveMcpServer(orphanedName).ConfigureAwait(false);
        }

        return JsonSerializer.SerializeToElement(new DepotSaveConnectionsAnswer(true), DepotChannel.Json);
    }

    // The live-refresh half of AC-501: a connection's memory source used to be registered once at Initialize and
    // never touched again, so an operator adding, renaming or removing a connection through the settings view saw
    // no effect until an app restart. Diffed by connection Id (not by list position, which a removal ahead of a
    // connection would shift out from under it).
    private static void _SyncMemorySources(ICockpitHost host, IReadOnlyList<DepotConnectionRegistration> before, IReadOnlyList<DepotConnectionRegistration> after)
    {
        // A plain Dictionary from BuildRegistrationPairs' own Connection.Id would throw on a duplicate key if
        // storage ever held two connections under the same id (corrupted or hand-edited settings) — building it by
        // hand keeps a duplicate merely overwriting the earlier entry instead of crashing this handler.
        var beforeById = new Dictionary<string, ProjectMemorySourceRegistration>(StringComparer.Ordinal);
        foreach (var pair in DepotMemorySource.BuildRegistrationPairs(before, host))
        {
            beforeById[pair.Connection.Id] = pair.Registration;
        }

        var afterPairs = DepotMemorySource.BuildRegistrationPairs(after, host);
        var afterById = new Dictionary<string, ProjectMemorySourceRegistration>(StringComparer.Ordinal);
        foreach (var pair in afterPairs)
        {
            afterById[pair.Connection.Id] = pair.Registration;
        }

        // Two full passes, not one interleaved remove-then-add per connection: two connections swapping names (or
        // otherwise trading schemes) would let an Add below claim a scheme a later connection in this same save
        // still held under its own before-registration, since that connection's own Remove had not run yet —
        // Register would then refuse the Add and the operator would lose that source from the picker until a
        // restart, with nothing surfacing why. Retiring every stale scheme first removes that ordering dependency.
        //
        // Plain == below, not a hand-rolled field comparison: ProjectMemorySourceRegistration's own equality
        // override (AC-502) already ignores ListLocationsAsync/SignInAsync/CheckReachability — every call to
        // BuildRegistrationPairs builds fresh closures over its own connection even when nothing changed, and two
        // such closures are never delegate-equal, which is exactly what that override exists to look past.
        foreach (var (id, oldRegistration) in beforeById)
        {
            if (!afterById.TryGetValue(id, out var stillCurrent) || stillCurrent != oldRegistration)
            {
                host.RemoveProjectMemorySource(oldRegistration.Scheme);
            }
        }

        foreach (var (connection, newRegistration) in afterPairs)
        {
            if (beforeById.TryGetValue(connection.Id, out var oldRegistration) && oldRegistration == newRegistration)
            {
                // Unchanged: re-adding it would only hit Register's "scheme already taken" refusal, since this very
                // content is already the one registered.
                continue;
            }

            host.AddProjectMemorySource(newRegistration);
        }
    }

    // AC-245: the same before/after diff-by-connection-Id _SyncMemorySources runs, applied to shared-project
    // sources instead. A separate pass rather than folded into that method: the two registries are independent
    // (ISharedProjectSourceRegistry, not IProjectMemorySourceRegistry) and keeping the diff loops apart means a
    // future change to one shape never has to reason about the other's equality rules.
    private static void _SyncSharedProjectSources(ICockpitHost host, IReadOnlyList<DepotConnectionRegistration> before, IReadOnlyList<DepotConnectionRegistration> after)
    {
        // Diffed by (Key, Connection) rather than by Key alone: DepotSharedProjectSource is a plain class with no
        // value equality of its own, and the first connection's Key is always the bare "depot" scheme regardless of
        // its name — a rename of that one connection would leave Key identical before and after, so comparing Key
        // alone would skip the refresh and leave a stale SourceName registered until a restart. Connection (a
        // record) already carries the value equality that catches a name/URL change; Key alone still matters for
        // the two-connections-trading-schemes ordering case _SyncMemorySources documents above.
        var beforeById = new Dictionary<string, (DepotConnectionRegistration Connection, string Key)>(StringComparer.Ordinal);
        foreach (var pair in DepotMemorySource.BuildRegistrationPairs(before, host))
        {
            beforeById[pair.Connection.Id] = (pair.Connection, pair.Registration.Scheme);
        }

        var afterById = new Dictionary<string, (DepotConnectionRegistration Connection, string Key)>(StringComparer.Ordinal);
        foreach (var pair in DepotMemorySource.BuildRegistrationPairs(after, host))
        {
            afterById[pair.Connection.Id] = (pair.Connection, pair.Registration.Scheme);
        }

        // Retire every stale key first, same ordering reason _SyncMemorySources documents: two connections trading
        // keys in one save must not have the second's Add refused by the first's still-registered old key.
        foreach (var (id, oldEntry) in beforeById)
        {
            if (!afterById.TryGetValue(id, out var current) || current.Key != oldEntry.Key || current.Connection != oldEntry.Connection)
            {
                host.RemoveSharedProjectSource(oldEntry.Key);
            }
        }

        foreach (var (id, entry) in afterById)
        {
            if (beforeById.TryGetValue(id, out var oldEntry) && oldEntry.Key == entry.Key && oldEntry.Connection == entry.Connection)
            {
                // Unchanged: re-adding would only hit Register's "key already taken" refusal.
                continue;
            }

            host.AddSharedProjectSource(new DepotSharedProjectSource(entry.Connection, entry.Key, host));
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
    private static McpServerContribution _ContributionFor(DepotConnectionRegistration connection)
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
        foreach (var handler in _handlers)
        {
            handler.Dispose();
        }

        _handlers.Clear();
    }
}
