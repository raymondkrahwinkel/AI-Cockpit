using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugin.Depot.Settings;
using Cockpit.Plugin.Depot.Ui;

namespace Cockpit.Plugin.Depot;

/// <summary>
/// Depot as a project memory source (AC-165/166) plus, since AC-243, a settings view where an operator connects one
/// or more Depot instances. Each connection is contributed to the shared MCP registry as an OAuth
/// <see cref="McpServerContribution"/> (AC-500) — Depot has a single auth path, so the plugin never holds a
/// credential of its own; the host drives the sign-in and keeps the token. Reading and writing project memory still
/// happens through the Depot MCP inside the session itself. Since AC-501, each connection also registers its own
/// memory source (see <see cref="DepotMemorySource"/>) rather than one fixed registration shared by every instance;
/// this plugin contributes the connection, not a tool.
/// </summary>
public sealed class DepotPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "depot",
        DisplayName: "Depot",
        Author: "Cockpit",
        Description: "Lets a project's memory live in a Depot project instead of a folder, and connects one or more Depot instances so a session can reach them.");

    public void ConfigureServices(IServiceCollection services)
    {
        // No services of its own — the registrations below need nothing built.
    }

    public void Initialize(ICockpitHost host)
    {
        var settings = new DepotSettings(host.Storage);
        host.AddSettings(() => new DepotSettingsControl(host, settings));

        // No connections configured yet means no memory source at all (AC-501) — the row behaves exactly as it did
        // before this plugin existed, rather than always offering a fixed "Depot project" nothing points at yet.
        foreach (var registration in DepotMemorySource.BuildRegistrations(settings.Connections, host))
        {
            host.AddProjectMemorySource(registration);
        }

        // AC-500's upsert-by-name is idempotent: re-contributing every saved connection on each start refreshes the
        // shared MCP registry entry without waiting for a settings save, same as every other AddMcpServer caller.
        // Sequential, not one fire-and-forget call per connection: AddMcpServer does its own load-modify-save round
        // trip against the shared store with no locking across calls, so firing several at once for two different
        // connections would race — each reads the same stale snapshot and the last SaveAsync to finish silently
        // drops whichever connection lost the race.
        _ = _AddMcpServersSequentiallyAsync(host, settings.Connections);
    }

    private static async Task _AddMcpServersSequentiallyAsync(ICockpitHost host, IReadOnlyList<Model.DepotConnectionRegistration> connections)
    {
        foreach (var connection in connections)
        {
            await host.AddMcpServer(new McpServerContribution(
                Name: connection.McpServerName,
                Url: $"{connection.Url}/mcp")
            {
                OAuthAuthority = connection.Url,
            }).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        // Nothing to release: no timers, no clients, no subscriptions.
    }
}
