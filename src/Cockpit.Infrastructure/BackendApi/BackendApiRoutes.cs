using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Infrastructure.BackendApi;

// AC-1383 (F5.1): the backend API on the node listener's app. McpAuthMiddleware has already said who is calling;
// the group's filter then lets in only a connect key over HTTPS — this same app answers on loopback too, where the
// app key and session tokens pass that middleware — and holds each route to the capability it declares.
internal static class BackendApiRoutes
{
    public const int ApiVersion = 1;

    private const string ForbiddenDescription = "This cockpit endpoint is not available to this caller.";

    public static void Map(WebApplication app, IServiceProvider services)
    {
        var api = app.MapGroup("/api/v1");
        api.AddEndpointFilter(_DoorAsync);

        api.MapGet("/whoami", () =>
        {
            var caller = _Caller();
            return Results.Json(new
            {
                keyPrefix = caller.KeyPrefix,
                label = caller.Label,
                capability = _Name(caller.Capability),
                node = Environment.MachineName,
                apiVersion = ApiVersion,
            });
        }).RequireOperate();

        // The same fields as the node tool list_connect_keys, from the same list.
        api.MapGet("/keys", async (CancellationToken cancellationToken) =>
        {
            var caller = _Caller();
            var keys = await services.GetRequiredService<ConnectKeyVerifier>().ListAsync(cancellationToken).ConfigureAwait(false);
            if (services.GetService<NodeAccessAuditLog>() is { } audit)
            {
                await audit.RecordAsync(new NodeAccessAuditEntry(DateTimeOffset.UtcNow, caller.Credential, caller.KeyPrefix, caller.RemoteAddress, "api:list_keys", "called"), cancellationToken).ConfigureAwait(false);
            }

            return Results.Json(new
            {
                keys = keys.Select(entry => new
                {
                    prefix = entry.Key.Prefix,
                    label = entry.Key.Label,
                    capability = _Name(entry.Key.Capability),
                    isBootstrap = entry.Key.IsBootstrap,
                    createdAt = entry.Key.CreatedAt,
                    expiresAt = entry.Key.ExpiresAt,
                    revokedAt = entry.Key.RevokedAt,
                    lastUsedAt = entry.LastUsedAt,
                }),
            });
        }).RequireAdmin();
    }

    public static RouteHandlerBuilder RequireOperate(this RouteHandlerBuilder route) =>
        route.WithMetadata(new BackendApiCapability(ConnectKeyCapability.Operate));

    public static RouteHandlerBuilder RequireAdmin(this RouteHandlerBuilder route) =>
        route.WithMetadata(new BackendApiCapability(ConnectKeyCapability.Admin));

    // The API's one error shape, the same as the MCP door's refusals.
    public static IResult Error(int statusCode, string code, string description) =>
        Results.Text(JsonSerializer.Serialize(new { error = code, error_description = description }), "application/json", statusCode: statusCode);

    // Admin implies operate, as `_RefuseIfNotAdmin` has it for the node tools. A route that declares nothing is
    // admin's: forgetting the metadata closes a route rather than opening it.
    private static async ValueTask<object?> _DoorAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var required = http.GetEndpoint()?.Metadata.GetMetadata<BackendApiCapability>()?.Required ?? ConnectKeyCapability.Admin;
        var allowed = http.Request.IsHttps
            && McpRequestContext.CurrentNodeCaller is { ByConnectKey: true } caller
            && (caller.Capability == ConnectKeyCapability.Admin || required == ConnectKeyCapability.Operate);

        return allowed
            ? await next(context).ConfigureAwait(false)
            : Error(StatusCodes.Status403Forbidden, "forbidden", ForbiddenDescription);
    }

    // Only reached behind `_DoorAsync`, which has checked the caller is there.
    private static NodeCaller _Caller() =>
        McpRequestContext.CurrentNodeCaller ?? throw new InvalidOperationException("A backend API route ran without a connect-key caller.");

    private static string _Name(ConnectKeyCapability capability) => capability.ToString().ToLowerInvariant();
}

// AC-1383: what a backend API route asks of the connect key calling it, read by the group's door.
internal sealed record BackendApiCapability(ConnectKeyCapability Required);
