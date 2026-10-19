using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Core.Secrets;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions.Channels;
using Microsoft.Extensions.Logging;

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
            var loaded = services.GetService<PluginManager>()?.Loaded;
            return Results.Json(installed.Select(plugin => PluginListEntry.From(plugin, loaded)), ConnectKeyEndpoints.Json);
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
            return Results.Json(PluginInstallResponse.From(result, install.Version), ConnectKeyEndpoints.Json);
        }).RequireAdmin();

        // AC-1525: the client names the bytes its operator consented to; the server pins them only when they are the bytes
        // it computed itself. Without a sha256 only a registered plugin is switched, on its own pin.
        api.MapPut("/plugins/{folderId}/enabled", async (string folderId, HttpRequest request, CancellationToken cancellationToken) =>
        {
            if (await _TryReadEnabledAsync(request, cancellationToken).ConfigureAwait(false) is not { } body)
            {
                return BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "invalid_request", "The plugin request is invalid.");
            }

            var (enabled, consented) = body;
            var installed = await plugins().GetInstalledAsync(cancellationToken).ConfigureAwait(false);
            var plugin = installed.FirstOrDefault(candidate => string.Equals(candidate.Discovered.FolderId, folderId, StringComparison.Ordinal));
            var pin = consented is null
                ? plugin?.Registration?.PinnedSha256
                : string.Equals(consented, plugin?.Discovered.Sha256, StringComparison.OrdinalIgnoreCase) ? plugin?.Discovered.Sha256 : null;
            if (pin is null)
            {
                return BackendApiRoutes.Error(StatusCodes.Status404NotFound, "no_plugin", "That plugin is not installed on the server.");
            }

            await plugins().SetEnabledAsync(folderId, enabled, pin, cancellationToken).ConfigureAwait(false);
            return Results.Json(new { takesEffectAfterServerRestarts = true }, ConnectKeyEndpoints.Json);
        }).RequireAdmin().Audited("set_plugin_enabled", services);

        api.MapDelete("/plugins/{folderId}", async (string folderId, CancellationToken cancellationToken) =>
        {
            var installed = await plugins().GetInstalledAsync(cancellationToken).ConfigureAwait(false);
            if (!installed.Any(candidate => string.Equals(candidate.Discovered.FolderId, folderId, StringComparison.Ordinal)))
            {
                return BackendApiRoutes.Error(StatusCodes.Status404NotFound, "no_plugin", "That plugin is not installed on the server.");
            }

            await plugins().RemoveAsync(folderId, cancellationToken).ConfigureAwait(false);
            await _AuditAsync(services, "api:remove_plugin", cancellationToken).ConfigureAwait(false);
            return Results.Json(new { takesEffectAfterServerRestarts = true }, ConnectKeyEndpoints.Json);
        }).RequireAdmin();

        // AC-1511: the view a plugin offers an admin of this server reaches the plugin's backend part here. The JSON
        // goes to the handler as it came and the answer back as it went; the payload can hold a credential, so the
        // audit names the plugin and the action, and no log line or error answer carries any of it.
        api.MapPost("/plugins/{folderId}/channel/{action}", async (string folderId, string action, HttpRequest request, CancellationToken cancellationToken) =>
        {
            var installed = await plugins().GetInstalledAsync(cancellationToken).ConfigureAwait(false);
            var plugin = installed.FirstOrDefault(candidate => string.Equals(candidate.Discovered.FolderId, folderId, StringComparison.Ordinal));
            if (plugin is null || services.GetService<PluginChannelHub>() is not { } channels)
            {
                return BackendApiRoutes.Error(StatusCodes.Status404NotFound, "no_plugin", "That plugin is not installed on the server.");
            }

            var pluginId = plugin.Discovered.Manifest.Id;
            JsonElement payload;
            try
            {
                using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken).ConfigureAwait(false);
                payload = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                return BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "invalid_request", "The plugin request is invalid.");
            }

            await _AuditAsync(services, "api:plugin_channel", cancellationToken, $"{pluginId}/{action}").ConfigureAwait(false);
            try
            {
                return Results.Json(await channels.InvokeAsync(pluginId, action, payload, cancellationToken).ConfigureAwait(false), ConnectKeyEndpoints.Json);
            }
            catch (PluginChannelUnknownActionException)
            {
                return BackendApiRoutes.Error(StatusCodes.Status404NotFound, "no_action", "That plugin offers no such action on the server.");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The type only: a handler's message may quote what it was given.
                services.GetService<ILoggerFactory>()?.CreateLogger(nameof(PluginEndpoints))
                    .LogWarning("Plugin {PluginId}'s channel action {Action} failed: {Failure}.", pluginId, action, exception.GetType().Name);
                return BackendApiRoutes.Error(StatusCodes.Status500InternalServerError, "plugin_failed", "The plugin could not handle that request.");
            }
        }).RequireAdmin();

        // AC-1571: the storage slice a plugin's own settings view reads, from the storage its running backend part reads, so
        // the next Set of that plugin cannot overwrite what was saved here. Every set credential reads as KeptSecret.
        api.MapGet("/plugins/{folderId}/storage", async (string folderId, CancellationToken cancellationToken) =>
        {
            if (_LiveStorage(services, folderId) is not var (pluginId, storage))
            {
                return _NotRunning();
            }

            var slice = _Slice(storage.Snapshot());
            await _AuditAsync(services, "api:get_plugin_storage", cancellationToken, pluginId).ConfigureAwait(false);
            return Results.Json(slice, ConnectKeyEndpoints.Json);
        }).RequireAdmin();

        // AC-1571: the keys a settings view changed. A kept secret stays as stored and an empty one clears it (AC-1534);
        // `secrets` names what the view wrote with SetSecret, so the server declares it as it would locally. The audit
        // names the keys, never a value.
        api.MapPut("/plugins/{folderId}/storage", async (string folderId, HttpRequest request, CancellationToken cancellationToken) =>
        {
            if (await _TryReadStorageAsync(request, cancellationToken).ConfigureAwait(false) is not var (values, removed, secrets))
            {
                return BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "invalid_request", "The storage request is invalid.");
            }

            if (_LiveStorage(services, folderId) is not var (pluginId, storage))
            {
                return _NotRunning();
            }

            var held = _Slice(storage.Snapshot());
            var stored = new JsonObject();
            foreach (var key in values.Select(property => property.Key))
            {
                stored[key] = held[key]?.DeepClone();
            }

            if (!ProfileEndpoints.KeepSecrets(stored, values, name => _IsSecret(name) || secrets.Contains(name)))
            {
                return ProfileEndpoints.KeyAgain;
            }

            foreach (var (key, value) in values)
            {
                if (secrets.Contains(key) && value is JsonValue text && text.TryGetValue<string>(out var secret))
                {
                    storage.SetSecret(key, secret);
                }
                else
                {
                    storage.Set(key, value);
                }
            }

            foreach (var key in removed)
            {
                storage.Remove(key);
            }

            var changed = string.Join(", ", values.Select(property => property.Key).Concat(removed));
            await _AuditAsync(services, "api:set_plugin_storage", cancellationToken, $"{pluginId}: {changed}").ConfigureAwait(false);
            return Results.Json(new JsonObject(), ConnectKeyEndpoints.Json);
        }).RequireAdmin();
    }

    private static bool _IsSecret(string name) => SecretKeyHolder.Shared.Fields.IsSecret(name);

    private static IResult _NotRunning() =>
        BackendApiRoutes.Error(StatusCodes.Status404NotFound, "no_plugin", "That plugin is not running on the server; enable it and restart the server first.");

    // The storage the plugin's backend part holds, only while it runs: writing the file under a running plugin would be
    // undone by its next Set, and a plugin that is not running has no one to read it yet.
    private static (string PluginId, PluginStorage Storage)? _LiveStorage(IServiceProvider services, string folderId) =>
        services.GetService<PluginManager>() is { } manager
        && manager.HostOf(folderId)?.Storage is PluginStorage storage
        && manager.Loaded.FirstOrDefault(plugin => string.Equals(plugin.FolderId, folderId, StringComparison.Ordinal)) is { } loaded
            ? (loaded.Manifest.Id, storage)
            : null;

    // Each stored value is JSON text; a value that does not parse goes as the text it is.
    private static JsonObject _Slice(IReadOnlyDictionary<string, string> values)
    {
        var slice = new JsonObject();
        foreach (var (key, json) in values)
        {
            try
            {
                slice[key] = JsonNode.Parse(json);
            }
            catch (JsonException)
            {
                slice[key] = json;
            }
        }

        return slice;
    }

    private static async Task<(JsonObject Values, IReadOnlyList<string> Removed, IReadOnlySet<string> Secrets)?> _TryReadStorageAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (await JsonNode.ParseAsync(request.Body, cancellationToken: cancellationToken).ConfigureAwait(false) is not JsonObject root
                || root.Any(property => property.Key is not ("values" or "removed" or "secrets"))
                || root["values"] is not JsonObject values
                || _Names(root["removed"]) is not { } removed
                || _Names(root["secrets"]) is not { } secrets
                || values.Any(property => property.Key.Length == 0))
            {
                return null;
            }

            return (values, removed, secrets.ToHashSet(StringComparer.Ordinal));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // A missing list is empty; anything but an array of non-empty strings is not a list of keys.
    private static List<string>? _Names(JsonNode? node) => node switch
    {
        null => [],
        JsonArray names when names.All(name => name is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0) =>
            [.. names.Select(name => name?.GetValue<string>() ?? "")],
        _ => null,
    };

    private static async Task _AuditAsync(IServiceProvider services, string action, CancellationToken cancellationToken, string? subject = null)
    {
        if (services.GetService<NodeAccessAuditLog>() is { } audit && McpRequestContext.CurrentNodeCaller is { } caller)
        {
            await audit.RecordAsync(NodeAccessAuditEntry.By(caller, DateTimeOffset.UtcNow, action, "called", subject), cancellationToken).ConfigureAwait(false);
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

            if (root.TryGetProperty("storeId", out var storeId)
                && root.TryGetProperty("pluginId", out var pluginId)
                && root.TryGetProperty("version", out var version)
                && storeId.ValueKind == JsonValueKind.String
                && pluginId.ValueKind == JsonValueKind.String
                && version.ValueKind == JsonValueKind.String
                && storeId.GetString() is string storeIdValue
                && pluginId.GetString() is string pluginIdValue
                && version.GetString() is string versionValue
                && !string.IsNullOrWhiteSpace(storeIdValue)
                && !string.IsNullOrWhiteSpace(pluginIdValue)
                && !string.IsNullOrWhiteSpace(versionValue))
            {
                return new PluginInstallRequest(storeIdValue, pluginIdValue, versionValue);
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<(bool Enabled, string? Sha256)?> _TryReadEnabledAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Any(property => property.Name is not ("enabled" or "sha256"))
                || !root.TryGetProperty("enabled", out var enabled)
                || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return null;
            }

            if (!root.TryGetProperty("sha256", out var sha256))
            {
                return (enabled.GetBoolean(), null);
            }

            return sha256.ValueKind == JsonValueKind.String && sha256.GetString() is { Length: > 0 } value ? (enabled.GetBoolean(), value) : null;
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
        if (!Uri.TryCreate(location, UriKind.Absolute, out var uri))
        {
            return location;
        }

        var builder = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty, Query = string.Empty };
        return builder.Uri.GetLeftPart(UriPartial.Path);
    }

    private sealed record PluginInstallRequest(string StoreId, string PluginId, string Version);

    // AC-1525: a plugin installed here is live only after the server restarts, and its consent step needs the folder and the
    // bytes the server computed.
    private sealed record PluginInstallResponse(PluginProvisionOutcome Outcome, string Id, string Name, string Version, string? Error, string? FolderId, string? Sha256, bool TakesEffectAfterServerRestarts)
    {
        public static PluginInstallResponse From(PluginProvisionResult result, string version) =>
            new(result.Outcome, result.Id, result.Name, version, result.IsSuccess ? null : "The server could not install the plugin.", result.FolderId, result.Sha256, result.IsSuccess);
    }

    // AC-1525: what the consent dialog shows, the server's own hash of the bytes, and whether this run already reflects the
    // decision on disk. The folder's path on the server stays here (AC-1474).
    private sealed record PluginListEntry(
        string FolderId,
        string Id,
        string Name,
        string Version,
        bool Enabled,
        string? AvailableUpdate,
        bool TakesEffectAfterServerRestarts,
        PluginLoadDecision Decision,
        string Sha256,
        string? PinnedSha256,
        string? Author,
        string? Description,
        string? MinHostVersion)
    {
        public static PluginListEntry From(InstalledPlugin plugin, IReadOnlyList<DiscoveredPlugin>? loaded)
        {
            var discovered = plugin.Discovered;
            var isLoaded = loaded?.Any(candidate => string.Equals(candidate.FolderId, discovered.FolderId, StringComparison.Ordinal)
                && string.Equals(candidate.Sha256, discovered.Sha256, StringComparison.OrdinalIgnoreCase)) ?? false;
            return new(
                discovered.FolderId,
                discovered.Manifest.Id,
                discovered.Manifest.Name,
                discovered.Manifest.Version,
                plugin.Registration?.Enabled ?? false,
                null,
                isLoaded != (discovered.Decision == PluginLoadDecision.Load),
                discovered.Decision,
                discovered.Sha256,
                plugin.Registration?.PinnedSha256,
                discovered.Manifest.Author,
                discovered.Manifest.Description,
                discovered.Manifest.MinHostVersion);
        }
    }

    private sealed record PluginStoreReference(string Id, string Location, PluginStoreIndex? Index);
}
