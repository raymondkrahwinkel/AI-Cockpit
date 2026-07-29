using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// A real MCP HTTP server (ModelContextProtocol.AspNetCore, Kestrel, loopback) gated behind an OAuth
/// authorization/resource-server pair shaped exactly like Depot's (AC-505): the protected-resource metadata
/// advertises only its own narrow scope (<c>scopes_supported: ["depot"]</c>), while a richer scope list —
/// including <c>offline_access</c> when <see cref="StartAsync"/> is asked to advertise it — lives only in the
/// authorization-server metadata. That split is the actual bug this ticket fixes: a client that derives its
/// requested scope from the wrong document never sees <c>offline_access</c> at all. Driving a real
/// <c>HttpClientTransport</c>/<c>McpClient.CreateAsync</c> handshake against this (rather than asserting on
/// <see cref="Cockpit.Infrastructure.Mcp.McpOAuthAuthorizer"/> options alone) is what makes the resulting test
/// prove the SDK's own scope negotiation, not just how Cockpit calls into it.
/// </summary>
internal sealed class InProcessOAuthMcpServer : IAsyncDisposable
{
    /// <summary>The bearer value every issued access token carries — the only one the resource gate accepts.</summary>
    public const string AccessToken = "test-access-token";

    /// <summary>The refresh token issued alongside <see cref="AccessToken"/> whenever offline_access was granted.</summary>
    public const string RefreshToken = "test-refresh-token";

    /// <summary>
    /// What a refresh-token grant renews to — distinct from <see cref="AccessToken"/> so a test can tell a real
    /// renewal apart from the resource gate simply accepting the original token again.
    /// </summary>
    public const string RenewedAccessToken = "test-renewed-access-token";

    /// <summary>The client id every dynamic client registration against this fixture is issued.</summary>
    public const string ClientId = "test-client";

    private readonly WebApplication _app;
    private readonly string?[] _lastRequestedScopeHolder;

    private InProcessOAuthMcpServer(WebApplication app, string baseUrl, string?[] lastRequestedScopeHolder)
    {
        _app = app;
        _lastRequestedScopeHolder = lastRequestedScopeHolder;
        BaseUrl = baseUrl;
        Url = $"{baseUrl}/mcp";
    }

    /// <summary>The server's own origin — issuer, resource and authorization-server base all in one, as Depot is.</summary>
    public string BaseUrl { get; }

    /// <summary>The <c>/mcp</c> endpoint URL, ready to use as an <see cref="Cockpit.Core.Mcp.McpServerConfig.Url"/>.</summary>
    public string Url { get; }

    /// <summary>
    /// The raw <c>scope</c> query parameter of the most recent <c>/connect/authorize</c> request — the only place a
    /// test can see what the client actually asked for, since the token exchange itself never repeats it on the wire
    /// (RFC 6749 §4.1.3). Proves a per-server <see cref="Cockpit.Core.Mcp.McpServerConfig.OAuthScopes"/> override
    /// (AC-505 criterion 3) replaced the derivation rather than merely adding to it.
    /// </summary>
    public string? LastRequestedScope => Volatile.Read(ref _lastRequestedScopeHolder[0]);

    public static async Task<InProcessOAuthMcpServer> StartAsync(bool advertiseOfflineAccess)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddMcpServer().WithHttpTransport().WithTools<McpTestToolA>();
        builder.WebHost.UseKestrel();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        var baseUrlHolder = new string[1];
        var lastRequestedScopeHolder = new string?[1];
        var scopeByCode = new ConcurrentDictionary<string, string>();

        // Registered before any Map* call, exactly like InProcessMcpHttpServer's delay middleware — that ordering is
        // what puts this ahead of endpoint routing, so it sees (and can gate) the /mcp requests below.
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/mcp"))
            {
                var presented = context.Request.Headers.Authorization.ToString();
                if (presented != $"Bearer {AccessToken}" && presented != $"Bearer {RenewedAccessToken}")
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.Headers.WWWAuthenticate =
                        $"Bearer resource_metadata=\"{Volatile.Read(ref baseUrlHolder[0])}/.well-known/oauth-protected-resource\"";
                    return;
                }
            }

            await next(context);
        });

        // RFC 9728 — deliberately narrow, matching the live Depot measurement (AC-505): only its own scope, never
        // offline_access, regardless of what the authorization server below advertises.
        app.MapGet("/.well-known/oauth-protected-resource", () => Results.Json(new
        {
            resource = $"{Volatile.Read(ref baseUrlHolder[0])}/mcp",
            authorization_servers = new[] { Volatile.Read(ref baseUrlHolder[0]) },
            scopes_supported = new[] { "depot" },
            bearer_methods_supported = new[] { "header" },
        }));

        // RFC 8414 — the richer document; offline_access lives here only, toggled per test.
        app.MapGet("/.well-known/oauth-authorization-server", () => Results.Json(new
        {
            issuer = Volatile.Read(ref baseUrlHolder[0]),
            authorization_endpoint = $"{Volatile.Read(ref baseUrlHolder[0])}/connect/authorize",
            token_endpoint = $"{Volatile.Read(ref baseUrlHolder[0])}/connect/token",
            registration_endpoint = $"{Volatile.Read(ref baseUrlHolder[0])}/connect/register",
            grant_types_supported = new[] { "authorization_code", "refresh_token" },
            response_types_supported = new[] { "code" },
            code_challenge_methods_supported = new[] { "S256" },
            scopes_supported = advertiseOfflineAccess
                ? new[] { "openid", "offline_access", "depot" }
                : new[] { "openid", "depot" },
        }));

        // RFC 7591 DCR — Depot has no configured OAuthClientId either, so the authorizer always takes this path.
        app.MapPost("/connect/register", () => Results.Json(new
        {
            client_id = ClientId,
            client_secret = "test-secret",
            token_endpoint_auth_method = "client_secret_post",
            grant_types = new[] { "authorization_code", "refresh_token" },
            response_types = new[] { "code" },
        }));

        // Stands in for the consent screen: nobody is watching, so it grants immediately and redirects straight
        // back. The requested scope is captured against the issued code — it never reaches the token endpoint on
        // the wire (RFC 6749 §4.1.3 carries no scope parameter), so this is the only place a test can see what the
        // client actually asked for.
        app.MapGet("/connect/authorize", (HttpContext context) =>
        {
            var scope = context.Request.Query["scope"].ToString();
            var state = context.Request.Query["state"].ToString();
            var redirectUri = context.Request.Query["redirect_uri"].ToString();
            var code = Guid.NewGuid().ToString("N");
            scopeByCode[code] = scope;
            Volatile.Write(ref lastRequestedScopeHolder[0], scope);

            return Results.Redirect($"{redirectUri}?code={code}&state={Uri.EscapeDataString(state)}");
        });

        app.MapPost("/connect/token", async (HttpContext context) =>
        {
            var form = await context.Request.ReadFormAsync();

            // AC4: proves the refresh grant this ticket exists to make possible actually works end to end, not just
            // that a refresh token gets stored. A real authorization server would reject an unrecognised or expired
            // refresh token; this one only recognises the exact one it issued.
            if (form["grant_type"] == "refresh_token")
            {
                if (form["refresh_token"] != RefreshToken)
                {
                    return Results.BadRequest(new { error = "invalid_grant" });
                }

                return Results.Json(new
                {
                    access_token = RenewedAccessToken,
                    token_type = "Bearer",
                    expires_in = 3600,
                    refresh_token = RefreshToken,
                });
            }

            string? scope = null;
            if (form["grant_type"] == "authorization_code" && scopeByCode.TryGetValue(form["code"].ToString(), out var granted))
            {
                scope = granted;
            }

            // The real behaviour this whole ticket is about: a refresh token is only ever handed back when the
            // request actually carried offline_access — never unconditionally, or the test would pass without the fix.
            var refreshToken = scope?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("offline_access") == true
                ? RefreshToken
                : null;

            return Results.Json(new
            {
                access_token = AccessToken,
                token_type = "Bearer",
                expires_in = 3600,
                refresh_token = refreshToken,
                scope,
            });
        });

        app.MapMcp("/mcp");
        await app.StartAsync().ConfigureAwait(false);

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel did not expose its bound addresses.");
        Volatile.Write(ref baseUrlHolder[0], addresses.Addresses.First().TrimEnd('/'));

        return new InProcessOAuthMcpServer(app, Volatile.Read(ref baseUrlHolder[0]), lastRequestedScopeHolder);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}
