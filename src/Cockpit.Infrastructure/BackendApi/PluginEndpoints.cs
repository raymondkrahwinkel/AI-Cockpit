using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Infrastructure.BackendApi;

// AC-1474: plugins are administered on the server. A store id never reveals a configured credential.
internal static class PluginEndpoints
{
    public static void Map(RouteGroupBuilder api, IServiceProvider services)
    {
        IPluginAdministration plugins() => services.GetRequiredService<IPluginAdministration>();
        IPluginStoreConfigStore stores() => services.GetRequiredService<IPluginStoreConfigStore>();

        api.MapGet("/plugins", async (CancellationToken cancellationToken) =>
        {
            var installed = await plugins().GetInstalledAsync(cancellationToken).ConfigureAwait(false);
            await _AuditAsync(services, "api:list_plugins", cancellationToken).ConfigureAwait(false);
            return Results.Json(installed, ConnectKeyEndpoints.Json);
        }).RequireAdmin();

        api.MapGet("/plugins/store", async (CancellationToken cancellationToken) =>
        {
            var catalog = new List<PluginStoreReference>();
            foreach (var store in await stores().LoadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = _Id(store);
                // A collision uses the first configured store, matching POST's first-match lookup.
                if (catalog.Any(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal)))
                {
                    continue;
                }

                var index = await plugins().FetchStoreIndexAsync(store, cancellationToken).ConfigureAwait(false);
                catalog.Add(new PluginStoreReference(id, _LocationWithoutCredentials(store.Location), index.Index));
            }
            await _AuditAsync(services, "api:list_plugin_stores", cancellationToken).ConfigureAwait(false);
            return Results.Json(catalog, ConnectKeyEndpoints.Json);
        }).RequireAdmin();

        api.MapPost("/plugins", async (HttpRequest request, CancellationToken cancellationToken) =>
        {
            if (await _TryReadInstallAsync(request, cancellationToken).ConfigureAwait(false) is not { } install)
            {
                return BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "invalid_request", "The plugin request is invalid.");
            }

            var store = (await stores().LoadAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(candidate => string.Equals(_Id(candidate), install.StoreId, StringComparison.Ordinal));
            if (store is null)
            {
                return BackendApiRoutes.Error(StatusCodes.Status404NotFound, "no_store", "That plugin store is not configured on the server.");
            }

            var index = await plugins().FetchStoreIndexAsync(store, cancellationToken).ConfigureAwait(false);
            var entry = index.Index?.Plugins.FirstOrDefault(plugin => string.Equals(plugin.Id, install.PluginId, StringComparison.Ordinal));
            var version = entry?.Versions.FirstOrDefault(candidate => string.Equals(candidate.Version, install.Version, StringComparison.Ordinal));
            if (entry is null || version is null)
            {
                return BackendApiRoutes.Error(StatusCodes.Status404NotFound, "no_plugin", "That plugin version is not in the server's store.");
            }

            var result = await plugins().InstallFromStoreAsync(new PluginProvisionRequest(entry.Id, entry.Name, store, version), cancellationToken).ConfigureAwait(false);
            await _AuditAsync(services, "api:install_plugin", cancellationToken).ConfigureAwait(false);
            return Results.Json(result, ConnectKeyEndpoints.Json);
        }).RequireAdmin();

        api.MapPut("/plugins/{folderId}/enabled", async (string folderId, HttpRequest request, CancellationToken cancellationToken) =>
        {
            if (await _TryReadEnabledAsync(request, cancellationToken).ConfigureAwait(false) is not { } enabled)
            {
                return BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "invalid_request", "The plugin request is invalid.");
            }

            var installed = await plugins().GetInstalledAsync(cancellationToken).ConfigureAwait(false);
            var plugin = installed.FirstOrDefault(candidate => string.Equals(candidate.Discovered.FolderId, folderId, StringComparison.Ordinal));
            if (plugin?.Registration?.PinnedSha256 is not { } sha256)
            {
                return BackendApiRoutes.Error(StatusCodes.Status404NotFound, "no_plugin", "That plugin is not installed on the server.");
            }

            await plugins().SetEnabledAsync(folderId, enabled, sha256, cancellationToken).ConfigureAwait(false);
            await _AuditAsync(services, "api:set_plugin_enabled", cancellationToken).ConfigureAwait(false);
            return Results.Json(new { takesEffectAfterServerRestarts = true }, ConnectKeyEndpoints.Json);
        }).RequireAdmin();

        api.MapDelete("/plugins/{folderId}", async (string folderId, CancellationToken cancellationToken) =>
        {
            await plugins().RemoveAsync(folderId, cancellationToken).ConfigureAwait(false);
            await _AuditAsync(services, "api:remove_plugin", cancellationToken).ConfigureAwait(false);
            return Results.Json(new { takesEffectAfterServerRestarts = true }, ConnectKeyEndpoints.Json);
        }).RequireAdmin();
    }

    private static async Task _AuditAsync(IServiceProvider services, string action, CancellationToken cancellationToken)
    {
        if (services.GetService<NodeAccessAuditLog>() is { } audit && McpRequestContext.CurrentNodeCaller is { } caller)
        {
            await audit.RecordAsync(NodeAccessAuditEntry.By(caller, DateTimeOffset.UtcNow, action, "called"), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<PluginInstallRequest?> _TryReadInstallAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(property => property.Name is not ("storeId" or "pluginId" or "version")))
            {
                return null;
            }

            return root.TryGetProperty("storeId", out var storeId)
                && root.TryGetProperty("pluginId", out var pluginId)
                && root.TryGetProperty("version", out var version)
                && storeId.ValueKind == JsonValueKind.String
                && pluginId.ValueKind == JsonValueKind.String
                && version.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(storeId.GetString())
                && !string.IsNullOrWhiteSpace(pluginId.GetString())
                && !string.IsNullOrWhiteSpace(version.GetString())
                ? new PluginInstallRequest(storeId.GetString()!, pluginId.GetString()!, version.GetString()!)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<bool?> _TryReadEnabledAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.EnumerateObject().All(property => property.Name == "enabled")
                && root.TryGetProperty("enabled", out var enabled)
                && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? enabled.GetBoolean()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string _Id(PluginStoreConfig store) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{store.Kind}\n{_LocationWithoutCredentials(store.Location)}")));

    private static string _LocationWithoutCredentials(string location)
    {
        return location;
    }

    private sealed record PluginInstallRequest(string StoreId, string PluginId, string Version);

    private sealed record PluginStoreReference(string Id, string Location, PluginStoreIndex? Index);
}
