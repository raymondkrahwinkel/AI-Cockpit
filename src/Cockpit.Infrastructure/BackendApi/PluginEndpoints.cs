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
using Cockpit.Infrastructure.ManagedCli;
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
            catch (Exception exception) when (_IsInvalidRequest(exception))
            {
                return BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "invalid_request", "The plugin request is invalid.");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The type only: a handler's message may quote what it was given.
                services.GetService<ILoggerFactory>()?.CreateLogger(nameof(PluginEndpoints))
                    .LogWarning("Plugin {PluginId}'s channel action {Action} failed: {Failure}.", pluginId, action, exception.GetType().Name);
                return BackendApiRoutes.Error(StatusCodes.Status500InternalServerError, "plugin_failed", "The plugin could not handle that request.");
            }
        }).RequireAdmin();

        // AC-1571: the storage slice a plugin's own settings view reads. Every set credential reads as KeptSecret.
        api.MapGet("/plugins/{folderId}/storage", async (string folderId, CancellationToken cancellationToken) =>
        {
            if (await _OpenStorageAsync(services, folderId, cancellationToken).ConfigureAwait(false) is not { } opened)
            {
                return _NotInstalled();
            }

            using var hold = opened;

            var slice = _Slice(opened.Storage.Snapshot());
            ProfileEndpoints.Mask(slice, opened.IsSecret);
            await _AuditAsync(services, "api:get_plugin_storage", cancellationToken, opened.PluginId).ConfigureAwait(false);
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

            if (await _OpenStorageAsync(services, folderId, cancellationToken).ConfigureAwait(false) is not { } opened)
            {
                return _NotInstalled();
            }

            using var hold = opened;

            // The whole slice as it reads after the change, untouched credentials as kept: the address rule then also sees a
            // `url` key changed beside a `token` key the request left alone.
            var (pluginId, storage) = (opened.PluginId, opened.Storage);
            bool isSecret(string name) => opened.IsSecret(name) || secrets.Contains(name);
            var held = _Slice(storage.Snapshot());
            foreach (var key in removed)
            {
                held.Remove(key);
            }

            var next = (JsonObject)held.DeepClone();
            ProfileEndpoints.Mask(next, isSecret);
            foreach (var (key, value) in values)
            {
                next[key] = value?.DeepClone();
            }

            if (!ProfileEndpoints.KeepSecrets(held, next, isSecret))
            {
                return ProfileEndpoints.KeyAgain;
            }

            foreach (var key in values.Select(property => property.Key))
            {
                var value = next[key];
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

            await opened.Written().ConfigureAwait(false);
            var changed = string.Join(", ", values.Select(property => property.Key).Concat(removed));
            await _AuditAsync(services, "api:set_plugin_storage", cancellationToken, $"{pluginId}: {changed}").ConfigureAwait(false);
            return Results.Json(new JsonObject(), ConnectKeyEndpoints.Json);
        }).RequireAdmin();

        // AC-1571: what a plugin's settings view asks its host about an MCP server it registered, answered by that plugin's
        // host here: the state only, never a token. Depot's rows show it (AC-1572).
        api.MapGet("/plugins/{folderId}/mcp-servers/{name}/auth-state", async (string folderId, string name, CancellationToken cancellationToken) =>
        {
            if (services.GetService<PluginManager>()?.HostOf(folderId) is not { } host)
            {
                return _NotRunning();
            }

            var state = await host.GetMcpServerAuthStateAsync(name, cancellationToken).ConfigureAwait(false);
            return Results.Json(new { state }, ConnectKeyEndpoints.Json);
        }).RequireAdmin().Audited("get_plugin_mcp_auth_state", services);

        // AC-1571 B2: the browser sign-in of a plugin's MCP server, run here with the PKCE verifier and the state kept here.
        // The desktop names its loopback port, opens the link and sends back the redirect's query; the token never leaves.
        McpServerSignIns mcpSignIns() => services.GetRequiredService<McpServerSignIns>();

        api.MapPost("/plugins/{folderId}/mcp-servers/{name}/sign-in", async (string folderId, string name, McpSignInStartBody body, CancellationToken cancellationToken) =>
        {
            if (body.Port is < 1024 or > 65535)
            {
                return BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "invalid_request", "port must be a loopback port from 1024 to 65535.");
            }

            if (services.GetService<PluginManager>()?.HostOf(folderId) is not { } host)
            {
                return _NotRunning();
            }

            var caller = McpRequestContext.CurrentNodeCaller;
            var signIn = await mcpSignIns().StartAsync(
                folderId,
                name,
                body.Port,
                ct => host.SignInMcpServerAsync(name, ct),
                ended => _McpSignInEndedAsync(services, caller, ended),
                cancellationToken).ConfigureAwait(false);
            return Results.Json(McpSignInView(signIn), ConnectKeyEndpoints.Json, statusCode: StatusCodes.Status201Created);
        }).RequireAdmin().Audited("start_plugin_mcp_sign_in", services);

        api.MapPost("/plugins/{folderId}/mcp-servers/{name}/sign-in/{flowId}/input", (string folderId, string name, string flowId, SignInInputBody body) =>
        {
            if (string.IsNullOrWhiteSpace(body.Text))
            {
                return BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "invalid_request", "text is required.");
            }

            if (mcpSignIns().Find(folderId, name, flowId) is not { } signIn)
            {
                return _NoMcpSignIn();
            }

            return signIn.Submit(body.Text)
                ? Results.Json(McpSignInView(signIn), ConnectKeyEndpoints.Json)
                : BackendApiRoutes.Error(StatusCodes.Status409Conflict, "not_awaited", "This sign-in is not waiting for that redirect.");
        }).RequireAdmin().Audited("send_plugin_mcp_sign_in_redirect", services);

        api.MapGet("/plugins/{folderId}/mcp-servers/{name}/sign-in/{flowId}", (string folderId, string name, string flowId) =>
            mcpSignIns().Find(folderId, name, flowId) is { } signIn
                ? Results.Json(McpSignInView(signIn), ConnectKeyEndpoints.Json)
                : _NoMcpSignIn()).RequireAdmin();

        // The desktop gave up (no browser, the wait ended, a link it would not open): ended now, the old token put back.
        api.MapDelete("/plugins/{folderId}/mcp-servers/{name}/sign-in/{flowId}", (string folderId, string name, string flowId) =>
        {
            if (mcpSignIns().Find(folderId, name, flowId) is not { } signIn)
            {
                return _NoMcpSignIn();
            }

            signIn.End("cancelled");
            return Results.Json(new JsonObject(), ConnectKeyEndpoints.Json);
        }).RequireAdmin().Audited("end_plugin_mcp_sign_in", services);

        // AC-1585: the managed command-line tools as the plugin's host on the server has them, so its own view (the
        // Claude provider's install panel) shows and changes the server's copy, not this desktop's.
        api.MapGet("/plugins/{folderId}/managed-clis", (string folderId) =>
            services.GetService<PluginManager>()?.HostOf(folderId) is { } host
                ? Results.Json((services.GetService<IManagedCliService>()?.RegisteredCliNames ?? []).ToDictionary(name => name, host.ResolveManagedCliPath), ConnectKeyEndpoints.Json)
                : _NotRunning()).RequireAdmin().Audited("list_plugin_managed_clis", services);

        api.MapGet("/plugins/{folderId}/managed-clis/{name}/status", async (string folderId, string name, CancellationToken cancellationToken) =>
        {
            if (services.GetService<PluginManager>()?.HostOf(folderId) is not { } host || !_IsManagedCli(services, name))
            {
                return _NoManagedCli();
            }

            var status = await host.GetManagedCliStatusAsync(name, cancellationToken).ConfigureAwait(false);
            var autoUpdate = await host.GetManagedCliAutoUpdateAsync(name, cancellationToken).ConfigureAwait(false);
            return Results.Json(new RemoteManagedCliStatus(status.InstalledVersion, status.LatestVersion, autoUpdate), ConnectKeyEndpoints.Json);
        }).RequireAdmin().Audited("get_plugin_managed_cli_status", services);

        api.MapPost("/plugins/{folderId}/managed-clis/{name}/install", async (string folderId, string name, CancellationToken cancellationToken) =>
        {
            if (services.GetService<PluginManager>()?.HostOf(folderId) is not { } host || !_IsManagedCli(services, name))
            {
                return _NoManagedCli();
            }

            var result = await host.InstallManagedCliAsync(name, cancellationToken).ConfigureAwait(false);
            return Results.Json(new RemoteManagedCliInstall(result.Success, result.Version, result.ExecutablePath, result.Error), ConnectKeyEndpoints.Json);
        }).RequireAdmin().Audited("install_plugin_managed_cli", services);

        api.MapDelete("/plugins/{folderId}/managed-clis/{name}", (string folderId, string name) =>
            services.GetService<PluginManager>()?.HostOf(folderId) is { } host && _IsManagedCli(services, name)
                ? Results.Json(new { removed = host.RemoveManagedCli(name) }, ConnectKeyEndpoints.Json)
                : _NoManagedCli()).RequireAdmin().Audited("remove_plugin_managed_cli", services);

        api.MapPut("/plugins/{folderId}/managed-clis/{name}/auto-update", async (string folderId, string name, ManagedCliAutoUpdateBody body, CancellationToken cancellationToken) =>
        {
            if (services.GetService<PluginManager>()?.HostOf(folderId) is not { } host || !_IsManagedCli(services, name))
            {
                return _NoManagedCli();
            }

            await host.SetManagedCliAutoUpdateAsync(name, body.Enabled, cancellationToken).ConfigureAwait(false);
            return Results.Json(new { ok = true }, ConnectKeyEndpoints.Json);
        }).RequireAdmin().Audited("set_plugin_managed_cli_auto_update", services);
    }

    private static bool _IsManagedCli(IServiceProvider services, string name) =>
        services.GetService<IManagedCliService>()?.RegisteredCliNames.Contains(name, StringComparer.Ordinal) == true;

    // Never the name it was given, which a mistyped path could have put anything in.
    private static IResult _NoManagedCli() =>
        BackendApiRoutes.Error(StatusCodes.Status404NotFound, "no_managed_cli", "The server manages no such command-line tool for a running plugin.");

    private static bool _IsSecret(string name) => SecretKeyHolder.Shared.Fields.IsSecret(name);

    // A channel request that does not fit its action: null, a missing field, a field of the wrong type. A handler reading
    // it meets one of these (System.Text.Json and LINQ throw them), so the caller hears invalid_request instead of a
    // plugin failure. ponytail: by exception type; a handler that throws one of these for its own reasons reads the same.
    private static bool _IsInvalidRequest(Exception exception) =>
        exception is ArgumentException or JsonException or KeyNotFoundException or FormatException
            or (InvalidOperationException and not ObjectDisposedException);

    private static IResult _NotInstalled() =>
        BackendApiRoutes.Error(StatusCodes.Status404NotFound, "no_plugin", "That plugin is not installed on the server.");

    // Written out field by field, so nothing the sign-in holds crosses by default.
    internal static object McpSignInView(McpServerSignIn signIn) => new
    {
        flowId = signIn.FlowId,
        status = signIn.Status,
        url = signIn.Url?.AbsoluteUri,
        awaitsRedirect = signIn.AwaitsRedirect,
        expiresAt = signIn.ExpiresAt,
    };

    private static async Task _McpSignInEndedAsync(IServiceProvider services, NodeCaller? caller, McpServerSignIn signIn)
    {
        if (services.GetService<NodeAccessAuditLog>() is { } audit && caller is not null)
        {
            await audit.RecordAsync(
                NodeAccessAuditEntry.By(caller, DateTimeOffset.UtcNow, "api:plugin_mcp_sign_in", $"sign-in {signIn.Status}", $"{signIn.FolderId}: {signIn.Name}"),
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static IResult _NoMcpSignIn() =>
        BackendApiRoutes.Error(StatusCodes.Status404NotFound, "no_sign_in", "That sign-in is not running on the server; start it again.");

    private static IResult _NotRunning() =>
        BackendApiRoutes.Error(StatusCodes.Status404NotFound, "no_plugin", "That plugin is not running on the server; enable it and restart the server first.");

    // AC-1571: a running plugin's own storage, so its next Set cannot overwrite what was saved here; for an installed one
    // that is not running (disabled, or waiting for a restart) and was consented to, its slice in the server's cockpit.json,
    // read when it starts. `Written` waits for those file writes; a credential its manifest declares counts as one either way.
    private static async Task<OpenedStorage?> _OpenStorageAsync(IServiceProvider services, string folderId, CancellationToken cancellationToken)
    {
        var manager = services.GetService<PluginManager>();
        if (manager?.HostOf(folderId)?.Storage is PluginStorage live
            && manager.Loaded.FirstOrDefault(plugin => string.Equals(plugin.FolderId, folderId, StringComparison.Ordinal)) is { } loaded)
        {
            return new OpenedStorage(loaded.Manifest.Id, live, _IsSecret, () => Task.CompletedTask, null);
        }

        // A plugin awaiting consent has no registration; writing its slice would make one and change what it awaits.
        var installed = await services.GetRequiredService<IPluginAdministration>().GetInstalledAsync(cancellationToken).ConfigureAwait(false);
        if (installed.FirstOrDefault(plugin => string.Equals(plugin.Discovered.FolderId, folderId, StringComparison.Ordinal)) is not { Registration: not null } plugin)
        {
            return null;
        }

        // The file has no Set of its own to serialise writes: one load-change-write at a time, or two saves lose one.
        // ponytail: one gate for every not-running plugin; per plugin if admins ever configure several at once.
        await SliceFiles.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Declared before anything is written, as a loaded plugin's are at startup, so an encrypted store protects them.
            var declared = plugin.Discovered.Manifest.SecretKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (declared.Count > 0)
            {
                SecretKeyHolder.Shared.Declare(declared);
            }

            var store = services.GetRequiredService<IPluginRegistrationStore>();
            var secretFields = services.GetRequiredService<IPluginSecretFieldStore>();
            var writes = new List<Task>();
            var storage = new PluginStorage(
                await store.LoadDataAsync(folderId, cancellationToken).ConfigureAwait(false),
                data => writes.Add(store.SaveDataAsync(folderId, data, CancellationToken.None)),
                key =>
                {
                    SecretKeyHolder.Shared.Declare([key]);
                    writes.Add(secretFields.DeclareAsync(folderId, [key], CancellationToken.None));
                });
            return new OpenedStorage(plugin.Discovered.Manifest.Id, storage, name => _IsSecret(name) || declared.Contains(name), () => Task.WhenAll(writes), SliceFiles);
        }
        catch
        {
            SliceFiles.Release();
            throw;
        }
    }

    private static readonly SemaphoreSlim SliceFiles = new(1, 1);

    private sealed record OpenedStorage(string PluginId, PluginStorage Storage, Func<string, bool> IsSecret, Func<Task> Written, SemaphoreSlim? Gate) : IDisposable
    {
        public void Dispose() => Gate?.Release();
    }

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
                || values.Any(property => property.Key.Length == 0)
                || secrets.Any(name => !values.ContainsKey(name)))
            {
                return null;
            }

            return (values, removed, secrets.ToHashSet(StringComparer.Ordinal));
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            // ArgumentException: a key twice in one object.
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

internal sealed record ManagedCliAutoUpdateBody(bool Enabled);

internal sealed record McpSignInStartBody(int Port);
