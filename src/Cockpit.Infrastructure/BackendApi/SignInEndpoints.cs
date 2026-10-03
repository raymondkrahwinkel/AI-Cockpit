using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.BackendApi;

// AC-1357: signing a provider profile in on this machine, for an admin key elsewhere. The token stays here: a
// response carries the link, a device code, host text and a status; the audit names who, which profile and what.
internal static class SignInEndpoints
{
    public static void Map(RouteGroupBuilder api, IServiceProvider services)
    {
        ProfileSignIns signIns() => services.GetRequiredService<ProfileSignIns>();

        api.MapPost("/profiles/{label}/sign-in", async (string label, CancellationToken cancellationToken) =>
        {
            var caller = _Caller();
            var profiles = await services.GetRequiredService<ISessionProfileStore>().LoadAsync(cancellationToken).ConfigureAwait(false);
            if (profiles.FirstOrDefault(profile => string.Equals(profile.Label, label, StringComparison.Ordinal)) is not { } profile
                || !caller.AllowsProfile(profile.Label, services.GetRequiredService<INodePairingBroker>()))
            {
                return Results.NotFound();
            }

            ProfileSignIn? signIn;
            try
            {
                signIn = await signIns().StartAsync(profile, ended => _AuditAsync(services, caller, $"sign-in {ended.Status}", ended.Profile, CancellationToken.None), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                services.GetService<ILoggerFactory>()?.CreateLogger(typeof(SignInEndpoints)).LogWarning("Could not start the sign-in for profile {Profile} ({Error}).", profile.Label, exception.GetType().Name);
                await _AuditAsync(services, caller, "sign-in could not start", profile.Label, cancellationToken).ConfigureAwait(false);
                return BackendApiRoutes.Error(StatusCodes.Status500InternalServerError, "not_started", "The provider's sign-in could not be started on this machine.");
            }

            if (signIn is null)
            {
                return BackendApiRoutes.Error(StatusCodes.Status409Conflict, "no_sign_in", "This profile's provider offers no sign-in without a browser.");
            }

            await _AuditAsync(services, caller, "sign-in started", profile.Label, CancellationToken.None).ConfigureAwait(false);
            return Results.Json(_View(signIn), statusCode: StatusCodes.Status201Created);
        }).RequireAdmin();

        api.MapPost("/profiles/{label}/sign-in/{flowId}/input", async (string label, string flowId, SignInInputBody body, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(body.Text))
            {
                return BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "invalid_request", "text is required.");
            }

            if (!inScope(label) || signIns().Find(label, flowId) is not { Status: "running" } signIn)
            {
                return Results.NotFound();
            }

            try
            {
                await signIn.SubmitAsync(body.Text, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The CLI ended between the status read and this write; its stdin is gone.
                return BackendApiRoutes.Error(StatusCodes.Status409Conflict, "not_running", "This sign-in has already ended.");
            }

            await _AuditAsync(services, _Caller(), "sign-in input sent", label, cancellationToken).ConfigureAwait(false);
            return Results.Json(_View(signIn));
        }).RequireAdmin();

        api.MapGet("/profiles/{label}/sign-in/{flowId}", (string label, string flowId) =>
            inScope(label) && signIns().Find(label, flowId) is { } signIn ? Results.Json(_View(signIn)) : Results.NotFound()).RequireAdmin();

        // The start route's scope check, for the routes that name a flow: a profile outside the key's scope is a 404.
        bool inScope(string label) => _Caller().AllowsProfile(label, services.GetRequiredService<INodePairingBroker>());
    }

    // Written out field by field, so nothing the flow holds crosses by default.
    private static object _View(ProfileSignIn signIn) => new
    {
        flowId = signIn.FlowId,
        profile = signIn.Profile,
        status = signIn.Status,
        message = signIn.Message,
        url = signIn.Url,
        code = signIn.Code,
        awaitsInput = signIn.AwaitsInput,
        expiresAt = signIn.ExpiresAt,
        error = signIn.Error,
    };

    private static async Task _AuditAsync(IServiceProvider services, NodeCaller caller, string outcome, string profile, CancellationToken cancellationToken)
    {
        if (services.GetService<NodeAccessAuditLog>() is { } audit)
        {
            await audit.RecordAsync(
                new NodeAccessAuditEntry(DateTimeOffset.UtcNow, caller.Credential, caller.KeyPrefix, caller.RemoteAddress, "api:sign_in", outcome, profile),
                cancellationToken).ConfigureAwait(false);
        }
    }

    // Only reached behind the group's door, which has checked the caller is there.
    private static NodeCaller _Caller() =>
        McpRequestContext.CurrentNodeCaller ?? throw new InvalidOperationException("A sign-in route ran without a connect-key caller.");
}

internal sealed record SignInInputBody(string Text);
