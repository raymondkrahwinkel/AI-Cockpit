using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Abstractions.Events;
using Cockpit.Core.Plugins;
using Cockpit.Plugins.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cockpit.Infrastructure.BackendApi;

internal static class PluginStoreEndpoints
{
    internal const string EventKind = "plugin-inventory";

    private static readonly JsonSerializerOptions RequestJson = new(ConnectKeyEndpoints.Json)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal static void Map(RouteGroupBuilder api, IServiceProvider services)
    {
        if (services.GetService<IPluginStoreSources>() is { } sources)
        {
            EventHandler changed = (_, _) => Changed(services);
            sources.SourcesChanged += changed;
            ((IEndpointRouteBuilder)api).ServiceProvider.GetRequiredService<IHostApplicationLifetime>()
                .ApplicationStopping.Register(() => sources.SourcesChanged -= changed);
        }

        api.MapGet("/plugins/store", async (CancellationToken cancellationToken) =>
        {
            var plugins = services.GetRequiredService<IPluginAdministration>();
            var catalog = new List<PluginStoreCatalogEntry>();
            foreach (var source in await SourcesAsync(services, cancellationToken).ConfigureAwait(false))
            {
                var store = source.Store;
                var id = Id(store);
                if (catalog.Any(entry => string.Equals(entry.Id, id, StringComparison.Ordinal)))
                {
                    continue;
                }
                var index = source.IsHidden || source.IsWithdrawn ? null
                    : (await plugins.FetchStoreIndexAsync(store, cancellationToken).ConfigureAwait(false)).Index;
                catalog.Add(new PluginStoreCatalogEntry(id, _LocationWithoutCredentials(store.Location), index,
                    store.Kind, source.Name, store.Signed?.SourceKey, store.Signed?.Organization, source.IsHidden, source.IsWithdrawn,
                    new PluginStoreTarget(AbstractionsContract.Version, HostVersionInfo.Current, RuntimeInformation.RuntimeIdentifier)));
            }
            return Results.Json(catalog, ConnectKeyEndpoints.Json);
        }).RequireAdmin().Audited("list_plugin_stores", services);

        api.MapPost("/plugins/store", async (HttpRequest request, CancellationToken cancellationToken) =>
        {
            var body = await _ReadAsync<StoreRequest>(request, cancellationToken).ConfigureAwait(false);
            if (body is null || string.IsNullOrWhiteSpace(body.Location) || !Enum.IsDefined(body.Kind))
            {
                return _Invalid();
            }
            var location = body.Location.Trim();
            if (body.Kind == PluginStoreKind.Local
                ? !Path.IsPathFullyQualified(location) || !Directory.Exists(location) || !string.IsNullOrEmpty(body.Token)
                : !PluginStoreUrl.TryResolveIndexUrl(location, out _, out _))
            {
                return _Invalid();
            }
            await services.GetRequiredService<IPluginStoreConfigStore>()
                .AddAsync(new PluginStoreConfig(body.Kind, location, string.IsNullOrWhiteSpace(body.Token) ? null : body.Token), cancellationToken).ConfigureAwait(false);
            Changed(services);
            return Results.Json(new { ok = true }, ConnectKeyEndpoints.Json);
        }).RequireAdmin().Audited("add_plugin_store", services);

        api.MapDelete("/plugins/store/{storeId}", async (string storeId, CancellationToken cancellationToken) =>
        {
            var stores = services.GetRequiredService<IPluginStoreConfigStore>();
            var store = (await stores.LoadAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(candidate => string.Equals(Id(candidate), storeId, StringComparison.Ordinal));
            if (store is null)
            {
                return _Missing();
            }
            await stores.RemoveAsync(store, cancellationToken).ConfigureAwait(false);
            Changed(services);
            return Results.Json(new { ok = true }, ConnectKeyEndpoints.Json);
        }).RequireAdmin().Audited("remove_plugin_store", services);

        api.MapPut("/plugins/store/{storeId}/hidden", async (string storeId, HttpRequest request, CancellationToken cancellationToken) =>
        {
            var body = await _ReadAsync<VisibilityRequest>(request, cancellationToken).ConfigureAwait(false);
            if (body is null)
            {
                return _Invalid();
            }
            var sources = services.GetService<IPluginStoreSources>();
            var source = (await SourcesAsync(services, cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(candidate => string.Equals(Id(candidate.Store), storeId, StringComparison.Ordinal));
            if (sources is null || source?.Store.Signed is not { } signed)
            {
                return _Missing();
            }
            await sources.SetHiddenAsync(signed.SourceKey, body.Hidden, cancellationToken).ConfigureAwait(false);
            Changed(services);
            return Results.Json(new { ok = true }, ConnectKeyEndpoints.Json);
        }).RequireAdmin().Audited("set_plugin_store_hidden", services);
    }

    internal static async Task<IReadOnlyList<PluginStoreSourceInfo>> SourcesAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.GetService<IPluginStoreSources>() is { } sources
            ? await sources.LoadAsync(cancellationToken).ConfigureAwait(false)
            : (await services.GetRequiredService<IPluginStoreConfigStore>().LoadAsync(cancellationToken).ConfigureAwait(false))
                .Select(store => new PluginStoreSourceInfo(store)).ToList();

    internal static string Id(PluginStoreConfig store) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        store.Signed is { } signed ? $"source\n{signed.SourceKey}" : $"{store.Kind}\n{_LocationWithoutCredentials(store.Location)}")));

    internal static void Changed(IServiceProvider services) =>
        services.GetService<IBackendEventLog>()?.Append(EventKind, null, new { changed = true });

    internal static string? SafeSource(string? source) => source?.StartsWith("remote:", StringComparison.Ordinal) == true
        ? PluginStoreConfig.Remote(_LocationWithoutCredentials(source["remote:".Length..])).SourceKey : source;

    private static string _LocationWithoutCredentials(string location)
    {
        if (!Uri.TryCreate(location, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return location;
        }
        return new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty, Query = string.Empty, Fragment = string.Empty }
            .Uri.GetLeftPart(UriPartial.Path);
    }

    private static async Task<T?> _ReadAsync<T>(HttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(request.Body, RequestJson, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static IResult _Invalid() => BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "invalid_request", "The plugin store request is invalid.");
    private static IResult _Missing() => BackendApiRoutes.Error(StatusCodes.Status404NotFound, "no_store", "That plugin store is not configured on the server.");

    private sealed record StoreRequest(PluginStoreKind Kind, string Location, string? Token = null);
    private sealed record VisibilityRequest([property: JsonRequired] bool Hidden);
}
