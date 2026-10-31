using Cockpit.Infrastructure.Clones;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.Infrastructure.BackendApi;

internal static class GitCredentialEndpoints
{
    public static void Map(RouteGroupBuilder api, IServiceProvider services)
    {
        GitHubGitSignIns signIns() => services.GetRequiredService<GitHubGitSignIns>();

        api.MapGet("/git-credentials/github", async (CancellationToken cancellationToken) =>
        {
            var state = await signIns().StatusAsync(cancellationToken).ConfigureAwait(false);
            return Results.Json(new { available = state.Available, signedIn = state.SignedIn, user = state.User, source = state.Source, message = state.Message });
        }).RequireAdmin();

        api.MapPost("/git-credentials/github/sign-in", async (GitHubSignInBody body, CancellationToken cancellationToken) =>
        {
            if (body.Mode is not (null or "device" or "token"))
            {
                return BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "invalid_request", "mode must be device or token.");
            }

            if (GitHubGitSignIns.EnvironmentSource is not null)
            {
                return _EnvironmentCredential();
            }

            var caller = _Caller();
            var signIn = await signIns().StartAsync(body.Mode == "token", ended => _AuditAsync(services, caller, $"sign-in {ended.Status}"), cancellationToken).ConfigureAwait(false);
            await _AuditAsync(services, caller, "sign-in started").ConfigureAwait(false);
            return Results.Json(_View(signIn), statusCode: StatusCodes.Status201Created);
        }).RequireAdmin();

        api.MapGet("/git-credentials/github/sign-in/{flowId}", (string flowId) =>
            signIns().Find(flowId) is { } signIn ? Results.Json(_View(signIn)) : _UnknownFlow()).RequireAdmin();

        api.MapPost("/git-credentials/github/sign-in/{flowId}/input", async (string flowId, SignInInputBody body, CancellationToken cancellationToken) =>
        {
            if (signIns().Find(flowId) is not { Status: "running" } signIn)
            {
                return BackendApiRoutes.Error(StatusCodes.Status404NotFound, "no_sign_in", body.Text);
            }

            if (string.IsNullOrWhiteSpace(body.Text) || body.Text.Length > 4096 || body.Text.Any(char.IsWhiteSpace) || body.Text.Any(char.IsControl))
            {
                return BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "invalid_request", "A token without whitespace is required.");
            }

            try
            {
                await signIn.SubmitAsync(body.Text, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return BackendApiRoutes.Error(StatusCodes.Status409Conflict, "not_running", "This sign-in is no longer awaiting input.");
            }

            await _AuditAsync(services, _Caller(), "sign-in input sent").ConfigureAwait(false);
            return Results.Json(_View(signIn));
        }).RequireAdmin();

        api.MapDelete("/git-credentials/github", async (CancellationToken cancellationToken) =>
        {
            if (GitHubGitSignIns.EnvironmentSource is not null)
            {
                return _EnvironmentCredential();
            }

            bool success;
            try
            {
                success = await signIns().LogoutAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                success = false;
            }

            await _AuditAsync(services, _Caller(), success ? "signed out" : "sign-out failed").ConfigureAwait(false);
            return success ? Results.NoContent() : BackendApiRoutes.Error(StatusCodes.Status409Conflict, "sign_out_failed", "GitHub could not be signed out on this server.");
        }).RequireAdmin();
    }

    // Explicit fields only: never serialize the process, raw CLI output, request body or credential.
    private static object _View(ProfileSignIn signIn) => new
    {
        flowId = signIn.FlowId,
        profile = "github",
        status = signIn.Status,
        message = signIn.AwaitsInput ? "Paste a GitHub token. It will be stored only on the server." : signIn.Message,
        url = signIn.Url,
        code = signIn.Code,
        awaitsInput = signIn.AwaitsInput,
        expiresAt = signIn.ExpiresAt,
        error = signIn.Error,
    };

    private static IResult _EnvironmentCredential() => BackendApiRoutes.Error(StatusCodes.Status409Conflict, "environment_credential", "GitHub uses a server environment credential. Change the server environment to replace or remove it.");

    private static IResult _UnknownFlow() => BackendApiRoutes.Error(StatusCodes.Status404NotFound, "no_sign_in", "This GitHub sign-in was not found.");

    private static Task _AuditAsync(IServiceProvider services, NodeCaller caller, string outcome) =>
        services.GetService<NodeAccessAuditLog>() is { } audit
            ? audit.RecordAsync(NodeAccessAuditEntry.By(caller, DateTimeOffset.UtcNow, "api:git_credentials", outcome, "github"), CancellationToken.None)
            : Task.CompletedTask;

    private static NodeCaller _Caller() => McpRequestContext.CurrentNodeCaller ?? throw new InvalidOperationException("A GitHub credential route ran without a connect-key caller.");
}

internal sealed record GitHubSignInBody(string? Mode);
