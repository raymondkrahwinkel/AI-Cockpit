using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Infrastructure.BackendApi;

// AC-1446 (F5.6b): the node tools' key management over HTTP, through the same IConnectKeyAdministration, which audits
// each change with the calling key as the actor. A full key leaves this server once: in the answer to its issue.
internal static class ConnectKeyEndpoints
{
    // Capabilities as "operate"/"admin", the words /whoami and the node tools use.
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private const int DefaultAuditPage = 200;

    public static void Map(RouteGroupBuilder api, IServiceProvider services)
    {
        IConnectKeyAdministration keys() => services.GetRequiredService<ConnectKeyVerifier>();

        api.MapGet("/keys", async (CancellationToken cancellationToken) =>
        {
            var overview = await keys().ListAsync(cancellationToken).ConfigureAwait(false);
            if (services.GetService<NodeAccessAuditLog>() is { } audit && McpRequestContext.CurrentNodeCaller is { } caller)
            {
                await audit.RecordAsync(NodeAccessAuditEntry.By(caller, DateTimeOffset.UtcNow, "api:list_keys", "called"), cancellationToken).ConfigureAwait(false);
            }

            return Results.Json(overview, Json);
        }).RequireAdmin();

        api.MapPost("/keys", async (HttpRequest request, CancellationToken cancellationToken) =>
        {
            ConnectKeyRequest? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<ConnectKeyRequest>(request.Body, Json, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                body = null;
            }

            if (body is null)
            {
                return BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "invalid_request", "The body must be a key request: label, capability, expiresInDays, holdsAssistant and scope.");
            }

            return await _AnswerAsync(async () => Results.Json(
                await keys().IssueAsync(body with { Scope = body.Scope ?? ConnectKeyScope.Default }, cancellationToken).ConfigureAwait(false),
                Json,
                statusCode: StatusCodes.Status201Created)).ConfigureAwait(false);
        }).RequireAdmin();

        api.MapDelete("/keys/{prefix}", (string prefix, CancellationToken cancellationToken) =>
            _AnswerAsync(async () => await keys().RevokeAsync(prefix, cancellationToken).ConfigureAwait(false)
                ? Results.Json(new { ok = true, prefix, revoked = true })
                : _NoKey(prefix))).RequireAdmin();

        api.MapPut("/keys/{prefix}/scope", async (string prefix, HttpRequest request, CancellationToken cancellationToken) =>
        {
            ConnectKeyScope? scope;
            try
            {
                scope = await JsonSerializer.DeserializeAsync<ConnectKeyScope>(request.Body, Json, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                scope = null;
            }

            if (scope is null)
            {
                return BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "invalid_request", "The body must be a scope.");
            }

            return await _AnswerAsync(async () => await keys().SetScopeAsync(prefix, scope, cancellationToken).ConfigureAwait(false)
                ? Results.Json(new { ok = true, prefix })
                : _NoKey(prefix)).ConfigureAwait(false);
        }).RequireAdmin();

        // AC-1459: the node tool lift_connect_lockout over HTTP. An IPv6 bucket's slash arrives escaped.
        api.MapPost("/lockouts/{bucket}/lift", (string bucket, CancellationToken cancellationToken) =>
        {
            var address = Uri.UnescapeDataString(bucket);
            return _AnswerAsync(async () => await keys().LiftLockoutAsync(address, cancellationToken).ConfigureAwait(false)
                ? Results.Json(new { ok = true, address, lifted = true })
                : BackendApiRoutes.Error(StatusCodes.Status404NotFound, "not_locked_out", "That address is not locked out."));
        }).RequireAdmin();

        // `before` is the id of the oldest entry already shown, not a time: equal timestamps cannot hide a line.
        api.MapGet("/audit", (long? before, int? count, CancellationToken cancellationToken) =>
            _AnswerAsync(async () => Results.Json(await keys().ReadAuditAsync(before, count ?? DefaultAuditPage, cancellationToken).ConfigureAwait(false), Json))).RequireAdmin();
    }

    // The contract's refusals as the API's one error shape: the bootstrap key is a conflict, a bad request a 400, and
    // a change that could not be saved a 500 that says so.
    private static async Task<IResult> _AnswerAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (ConnectKeyBootstrapException exception)
        {
            return BackendApiRoutes.Error(StatusCodes.Status409Conflict, "bootstrap_key", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "invalid_request", exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return BackendApiRoutes.Error(StatusCodes.Status500InternalServerError, "not_saved", exception.Message);
        }
    }

    private static IResult _NoKey(string prefix) =>
        BackendApiRoutes.Error(StatusCodes.Status404NotFound, "no_key", $"There is no live connect key with prefix '{prefix}'.");
}
