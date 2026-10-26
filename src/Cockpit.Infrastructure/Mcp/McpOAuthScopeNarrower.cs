using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Mcp;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Mcp;

// AC-1586: a refresh-token grant that asks for less than the grant holds (RFC 6749 §6), so a scope the cockpit keeps
// for itself never rides on a token a session is handed. No parameter or response body is ever logged: both carry
// credentials.
internal sealed class McpOAuthScopeNarrower(ILogger<McpOAuthScopeNarrower> logger, HttpMessageHandler? handler = null) : ISingletonService
{
    private readonly HttpClient _http = new(handler ?? new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(30) };

    public static IReadOnlyList<string> Split(string? scopes) =>
        (scopes ?? string.Empty).Split([' ', ',', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

    // Whether a session may be handed `token` for `server`: false while it carries, or may carry, a host-only scope.
    public static bool IsSessionSafe(McpServerConfig server, McpOAuthToken token) =>
        Split(server.OAuthHostOnlyScopes) is not { Count: > 0 } hostOnly
        || (token.Scope is not null && !Split(token.Scope).Intersect(hostOnly, StringComparer.Ordinal).Any());

    // What may be stored for `server` in place of `token`: itself when it is session-safe, else the same grant narrowed
    // to the scopes without the host-only ones. A failed narrowing keeps the refresh token and no usable access token,
    // so the next use renews instead of handing a session the wide one.
    public async Task<McpOAuthToken> KeepHostOnlyScopesOffAsync(McpServerConfig server, McpOAuthToken token, CancellationToken cancellationToken)
    {
        if (IsSessionSafe(server, token))
        {
            return token;
        }

        var hostOnly = Split(server.OAuthHostOnlyScopes);
        var granted = Split(token.Scope ?? server.OAuthScopes);
        var sessionScope = string.Join(' ', granted.Except(hostOnly, StringComparer.Ordinal));
        var narrowed = sessionScope.Length == 0 ? null : await NarrowAsync(server, token, sessionScope, cancellationToken).ConfigureAwait(false);
        if (narrowed is { AccessToken.Length: > 0 })
        {
            return narrowed;
        }

        logger.LogWarning(
            "MCP server {Server} gave no session token without its host-only scopes; the sign-in stays, but no session gets a token from it.",
            server.Name);
        return (narrowed ?? token) with { AccessToken = string.Empty, ExpiresAt = DateTimeOffset.UtcNow, Scope = null };
    }

    // Redeems `held`'s refresh token for `scope` alone. Null when it was refused or never sent (not spent); no refresh
    // token when the answer was lost; else the rotated one (or `held`'s, when none came back). The access token is
    // empty unless the server's stated scope is within `scope`.
    public async Task<McpOAuthToken?> NarrowAsync(McpServerConfig server, McpOAuthToken held, string scope, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(held.RefreshToken) || string.IsNullOrWhiteSpace(held.AuthorizationServer))
        {
            return null;
        }

        var sent = false;
        try
        {
            if (await TokenEndpointAsync(_http, held.AuthorizationServer, cancellationToken).ConfigureAwait(false) is not { } tokenEndpoint)
            {
                logger.LogWarning("MCP server {Server} names no token endpoint to narrow a token at.", server.Name);
                return null;
            }

            // One scope parameter: with two, OpenIddict does not narrow and silently answers with the whole grant.
            var form = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = held.RefreshToken,
                ["scope"] = scope,
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint);
            _AuthenticateClient(held, form, request);
            request.Content = new FormUrlEncodedContent(form);
            // Once sent, the server may have rotated the refresh token, so the answer is read whoever stopped waiting.
            sent = true;
            using var response = await _http.SendAsync(request, CancellationToken.None).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("MCP server {Server} refused to narrow a token to {Scope} ({Status}).", server.Name, scope, (int)response.StatusCode);
                return null;
            }

            using var answer = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(CancellationToken.None).ConfigureAwait(false),
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            var root = answer.RootElement;
            // RFC 6749 §5.1: a server leaves the scope out when it granted what was asked, as Depot does; one it does state
            // has to be within the request, which catches a server that ignored it and answered with the whole grant.
            var grantedScope = String(root, "scope") ?? scope;
            var withinAsked = !Split(grantedScope).Except(Split(scope), StringComparer.Ordinal).Any();
            if (!withinAsked)
            {
                logger.LogWarning("MCP server {Server} answered a request for {Scope} with {Granted}; that token is not used.", server.Name, scope, grantedScope);
            }

            DateTimeOffset? expiresAt = root.TryGetProperty("expires_in", out var expiresIn) && expiresIn.TryGetInt32(out var seconds) && seconds > 0
                ? DateTimeOffset.UtcNow.AddSeconds(seconds)
                : null;
            return held with
            {
                AccessToken = withinAsked ? String(root, "access_token") ?? string.Empty : string.Empty,
                Scheme = String(root, "token_type") is { Length: > 0 } scheme ? scheme : "Bearer",
                RefreshToken = String(root, "refresh_token") is { Length: > 0 } rotated ? rotated : held.RefreshToken,
                ExpiresAt = expiresAt,
                Scope = withinAsked ? grantedScope : null,
            };
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
            || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogWarning("Narrowing a token for MCP server {Server} failed: {Failure}.", server.Name, exception.GetType().Name);

            // Sent but unanswered: the refresh token may be spent, and replaying a spent one revokes the grant, so the
            // sign-in ends here instead.
            return sent ? held with { AccessToken = string.Empty, RefreshToken = null, ExpiresAt = DateTimeOffset.UtcNow, Scope = null } : null;
        }
    }

    // RFC 8414 first, OpenID Connect discovery second; Depot (OpenIddict) answers both.
    public static async Task<string?> TokenEndpointAsync(HttpClient http, string authorizationServer, CancellationToken cancellationToken)
    {
        var issuer = authorizationServer.TrimEnd('/');
        foreach (var candidate in new[] { $"{issuer}/.well-known/oauth-authorization-server", $"{issuer}/.well-known/openid-configuration" })
        {
            if (await ReadJsonAsync(http, candidate, cancellationToken).ConfigureAwait(false) is { } metadata
                && String(metadata, "token_endpoint") is { Length: > 0 } endpoint)
            {
                return endpoint;
            }
        }

        return null;
    }

    public static async Task<JsonElement?> ReadJsonAsync(HttpClient http, string url, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
    }

    public static string? String(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // The client the refresh token was issued to, presented the way it registered: a public client by id alone.
    private static void _AuthenticateClient(McpOAuthToken held, Dictionary<string, string> form, HttpRequestMessage request)
    {
        if (string.IsNullOrWhiteSpace(held.ClientId))
        {
            return;
        }

        if (string.IsNullOrEmpty(held.ClientSecret))
        {
            form["client_id"] = held.ClientId;
        }
        else if (string.Equals(held.TokenEndpointAuthMethod, "client_secret_post", StringComparison.Ordinal))
        {
            form["client_id"] = held.ClientId;
            form["client_secret"] = held.ClientSecret;
        }
        else
        {
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(held.ClientId)}:{Uri.EscapeDataString(held.ClientSecret)}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        }
    }
}
