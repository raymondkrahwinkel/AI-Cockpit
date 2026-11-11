using Cockpit.Core.Abstractions.SessionBehavior;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.Infrastructure.BackendApi;

// AC-2035: the server's automatic Docker clean-up switch on its admin API. It lives in `sessionBehavior` of the server's
// cockpit.json, which the storage maintenance reads on every tick, so a change applies without a restart.
internal static class StorageSettingsEndpoints
{
    private const string Route = "/storage/settings";

    public static void Map(RouteGroupBuilder api, IServiceProvider services)
    {
        ISessionBehaviorSettingsStore store() => services.GetRequiredService<ISessionBehaviorSettingsStore>();

        api.MapGet(Route, async (CancellationToken cancellationToken) =>
            Results.Json(new StorageSettingsBody((await store().LoadAsync(cancellationToken).ConfigureAwait(false)).AutomaticDockerCleanup), ConnectKeyEndpoints.Json))
            .RequireAdmin();

        api.MapPut(Route, async (HttpRequest request, CancellationToken cancellationToken) =>
        {
            var (body, refused) = await ProfileEndpoints.ReadBodyAsync<StorageSettingsBody>(request, "The body names only automaticDockerCleanup.", cancellationToken).ConfigureAwait(false);
            if (body is not { AutomaticDockerCleanup: { } enabled })
            {
                return body is null ? refused : BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "invalid_request", "The body names only automaticDockerCleanup.");
            }

            await OptionsSettingsEndpoints.EditGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var stored = await store().LoadAsync(cancellationToken).ConfigureAwait(false);
                await store().SaveAsync(stored with { AutomaticDockerCleanup = enabled }, cancellationToken).ConfigureAwait(false);
                return Results.Json(new StorageSettingsBody(enabled), ConnectKeyEndpoints.Json);
            }
            finally
            {
                OptionsSettingsEndpoints.EditGate.Release();
            }
        }).RequireAdmin().Audited("storage_settings", services);
    }
}

internal sealed record StorageSettingsBody(bool? AutomaticDockerCleanup);
