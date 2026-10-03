using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Core.Mcp;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Infrastructure.BackendApi;

// AC-1383 (F5.1): the backend API on the node listener's app. McpAuthMiddleware has already said who is calling;
// the group's filter then lets in only a connect key over HTTPS — this same app answers on loopback too, where the
// app key and session tokens pass that middleware — and holds each route to the capability it declares.
internal static class BackendApiRoutes
{
    public const int ApiVersion = 1;

    // AC-1458: when this process started, for the connecting side's uptime.
    internal static readonly DateTimeOffset StartedAt = _ProcessStart();

    private const string ForbiddenDescription = "This cockpit endpoint is not available to this caller.";

    public static void Map(WebApplication app, IServiceProvider services)
    {
        HealthzEndpoint.Map(app, services);

        var api = app.MapGroup("/api/v1");
        api.AddEndpointFilter(_DoorAsync);
        EventsEndpoint.Map(api, services);
        FilesEndpoint.Map(api, services);
        SessionsEndpoints.Map(api, services);
        SignInEndpoints.Map(api, services);
        HealthEndpoints.Map(api, services);

        api.MapGet("/whoami", async (CancellationToken cancellationToken) =>
        {
            var caller = _Caller();

            // AC-1456: who holds the assistant, by label only, so a key that does not can say where it went.
            var keys = await services.GetRequiredService<ConnectKeyVerifier>().ListAsync(cancellationToken).ConfigureAwait(false);
            var holder = keys.Select(entry => entry.Key)
                .FirstOrDefault(key => key.HoldsAssistant && key.IsUsableAt(DateTimeOffset.UtcNow))?.Label;
            return Results.Json(new
            {
                keyPrefix = caller.KeyPrefix,
                label = caller.Label,
                capability = CapabilityName(caller.Capability),
                node = Environment.MachineName,
                apiVersion = ApiVersion,
                expiresAt = caller.ExpiresAt,
                holdsAssistant = caller.HoldsAssistant,
                mayAnswerPermissions = caller.MayAnswerPermissions,
                version = HostVersion(),
                startedAt = StartedAt,
                assistantHeldBy = holder,
            });
        }).RequireOperate();

        // The same fields as the node tool list_connect_keys, from the same list.
        api.MapGet("/keys", async (CancellationToken cancellationToken) =>
        {
            var caller = _Caller();
            var verifier = services.GetRequiredService<ConnectKeyVerifier>();
            var keys = await verifier.ListAsync(cancellationToken).ConfigureAwait(false);
            var lockouts = await verifier.ListLockoutsAsync(cancellationToken).ConfigureAwait(false);
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
                    capability = CapabilityName(entry.Key.Capability),
                    isBootstrap = entry.Key.IsBootstrap,
                    createdAt = entry.Key.CreatedAt,
                    expiresAt = entry.Key.ExpiresAt,
                    revokedAt = entry.Key.RevokedAt,
                    lastUsedAt = entry.LastUsedAt,
                }),
                lockouts = lockouts.Select(lockout => new { address = lockout.Bucket, lockedUntil = lockout.LockedUntil, refusedWhileLockedOut = lockout.RefusedWhileLockedOut }),
            });
        }).RequireAdmin();

        // AC-1459: the node tool lift_connect_lockout over HTTP. An IPv6 bucket's slash arrives escaped.
        api.MapPost("/lockouts/{bucket}/lift", async (string bucket, CancellationToken cancellationToken) =>
        {
            try
            {
                return await services.GetRequiredService<ConnectKeyVerifier>().LiftLockoutAsync(Uri.UnescapeDataString(bucket), _Caller(), cancellationToken).ConfigureAwait(false)
                    ? Results.Json(new { ok = true, address = Uri.UnescapeDataString(bucket), lifted = true })
                    : Error(StatusCodes.Status404NotFound, "not_locked_out", "That address is not locked out.");
            }
            catch (InvalidOperationException exception)
            {
                return Error(StatusCodes.Status500InternalServerError, "not_saved", exception.Message);
            }
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
    // admin's: forgetting the metadata closes it. Named per capability, so a new lower one opens nothing.
    private static async ValueTask<object?> _DoorAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var required = http.GetEndpoint()?.Metadata.GetMetadata<BackendApiCapability>()?.Required ?? ConnectKeyCapability.Admin;
        var allowed = http.Request.IsHttps
            && McpRequestContext.CurrentNodeCaller is { ByConnectKey: true } caller
            && (caller.Capability == ConnectKeyCapability.Admin || (required == ConnectKeyCapability.Operate && caller.Capability == ConnectKeyCapability.Operate));

        return allowed
            ? await next(context).ConfigureAwait(false)
            : Error(StatusCodes.Status403Forbidden, "forbidden", ForbiddenDescription);
    }

    // Only reached behind `_DoorAsync`, which has checked the caller is there.
    private static NodeCaller _Caller() =>
        McpRequestContext.CurrentNodeCaller ?? throw new InvalidOperationException("A backend API route ran without a connect-key caller.");

    private static DateTimeOffset _ProcessStart()
    {
        using var process = Process.GetCurrentProcess();
        return process.StartTime.ToUniversalTime();
    }

    internal static string HostVersion() =>
        HostVersionInfo.Current.ToString(HostVersionInfo.Current.Build < 0 ? 2 : 3);

    internal static string CapabilityName(ConnectKeyCapability capability) => capability.ToString().ToLowerInvariant();
}

// AC-1383: what a backend API route asks of the connect key calling it, read by the group's door.
internal sealed record BackendApiCapability(ConnectKeyCapability Required);
