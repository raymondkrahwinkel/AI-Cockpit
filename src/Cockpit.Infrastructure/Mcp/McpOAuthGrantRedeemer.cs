using System.Net;
using System.Text.Json;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Plugins.Abstractions.Mcp;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Mcp;

// AC-1511: a sign-in without a browser. Redeems a plugin's grant at the token endpoint the server's metadata names,
// found as the MCP SDK finds it, and files the tokens where the SDK's sign-in does, with what renewing them needs.
// No parameter or response body is ever logged: both carry credentials.
internal sealed class McpOAuthGrantRedeemer(IMcpOAuthTokenStore tokenStore, ILogger<McpOAuthGrantRedeemer> logger) : ISingletonService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<PluginMcpSignInOutcome> RedeemAsync(
        McpServerConfig server,
        string grantType,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        if (server.Transport != McpTransport.Http || !Uri.TryCreate(server.Url, UriKind.Absolute, out var resource))
        {
            return PluginMcpSignInOutcome.Unavailable;
        }

        try
        {
            if (await _AuthorizationServerAsync(resource, cancellationToken).ConfigureAwait(false) is not { } authorizationServer
                || await _TokenEndpointAsync(authorizationServer, cancellationToken).ConfigureAwait(false) is not { } tokenEndpoint)
            {
                logger.LogWarning("MCP server {Server} names no token endpoint to redeem a grant at.", server.Name);
                return PluginMcpSignInOutcome.Unreachable;
            }

            var form = new Dictionary<string, string>(parameters, StringComparer.Ordinal) { ["grant_type"] = grantType };
            if (!form.ContainsKey("client_id") && !string.IsNullOrWhiteSpace(server.OAuthClientId))
            {
                form["client_id"] = server.OAuthClientId;
            }

            using var content = new FormUrlEncodedContent(form);
            using var response = await Http.PostAsync(tokenEndpoint, content, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                logger.LogWarning("MCP server {Server} refused a {GrantType} grant ({Status}).", server.Name, grantType, (int)response.StatusCode);
                return PluginMcpSignInOutcome.Declined;
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Redeeming a {GrantType} grant at MCP server {Server} failed ({Status}).", grantType, server.Name, (int)response.StatusCode);
                return PluginMcpSignInOutcome.Unreachable;
            }

            using var answer = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = answer.RootElement;
            if (_String(root, "access_token") is not { Length: > 0 } accessToken)
            {
                logger.LogWarning("MCP server {Server} accepted a {GrantType} grant but returned no access token.", server.Name, grantType);
                return PluginMcpSignInOutcome.Declined;
            }

            DateTimeOffset? expiresAt = root.TryGetProperty("expires_in", out var expiresIn) && expiresIn.TryGetInt32(out var seconds) && seconds > 0
                ? DateTimeOffset.UtcNow.AddSeconds(seconds)
                : null;
            var refreshToken = _String(root, "refresh_token");
            await tokenStore.SaveAsync(
                server.IdentityKey,
                server.Name,
                new McpOAuthToken
                {
                    AccessToken = accessToken,
                    Scheme = _String(root, "token_type") is { Length: > 0 } scheme ? scheme : "Bearer",
                    RefreshToken = refreshToken,
                    ExpiresAt = expiresAt,
                    Scope = _String(root, "scope"),
                    ResourceUrl = server.Url,
                    ClientId = form.GetValueOrDefault("client_id"),
                    AuthorizationServer = authorizationServer,
                },
                cancellationToken).ConfigureAwait(false);

            logger.LogInformation(
                "Stored a new access token for MCP server {Server} from a {GrantType} grant; it expires at {ExpiresAt} and {RefreshState}.",
                server.Name,
                grantType,
                expiresAt,
                string.IsNullOrWhiteSpace(refreshToken) ? "carries no refresh token, so it cannot renew itself" : "can renew itself");
            return PluginMcpSignInOutcome.Authorized;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
            || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogWarning("Redeeming a {GrantType} grant at MCP server {Server} failed: {Failure}.", grantType, server.Name, exception.GetType().Name);
            return PluginMcpSignInOutcome.Unreachable;
        }
    }

    // RFC 9728: the path-suffixed document first, the bare one second — the SDK's own order. The entry is kept as
    // written, since the SDK restores a stored client identity only for the authorization server it names itself.
    private static async Task<string?> _AuthorizationServerAsync(Uri resource, CancellationToken cancellationToken)
    {
        var origin = resource.GetLeftPart(UriPartial.Authority);
        var path = resource.AbsolutePath.TrimEnd('/');
        foreach (var candidate in path.Length > 0
            ? [$"{origin}/.well-known/oauth-protected-resource{path}", $"{origin}/.well-known/oauth-protected-resource"]
            : new[] { $"{origin}/.well-known/oauth-protected-resource" })
        {
            if (await _ReadJsonAsync(candidate, cancellationToken).ConfigureAwait(false) is { } metadata
                && metadata.TryGetProperty("authorization_servers", out var servers)
                && servers.ValueKind == JsonValueKind.Array
                && servers.EnumerateArray().Select(server => server.GetString()).FirstOrDefault(server => !string.IsNullOrWhiteSpace(server)) is { } first)
            {
                return first;
            }
        }

        return null;
    }

    // RFC 8414 first, OpenID Connect discovery second; Depot (OpenIddict) answers both.
    private static async Task<string?> _TokenEndpointAsync(string authorizationServer, CancellationToken cancellationToken)
    {
        var issuer = authorizationServer.TrimEnd('/');
        foreach (var candidate in new[] { $"{issuer}/.well-known/oauth-authorization-server", $"{issuer}/.well-known/openid-configuration" })
        {
            if (await _ReadJsonAsync(candidate, cancellationToken).ConfigureAwait(false) is { } metadata
                && _String(metadata, "token_endpoint") is { Length: > 0 } endpoint)
            {
                return endpoint;
            }
        }

        return null;
    }

    private static async Task<JsonElement?> _ReadJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
    }

    private static string? _String(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
