using Cockpit.Core.Mcp;
using Microsoft.AspNetCore.Http;

namespace Cockpit.Journeys;

// AC-1656/AC-1661: the Depot plugin's account endpoints spend a host-only depot.host token, which the host narrows from the
// sign-in's refresh token at Depot's token endpoint. A fake Depot answers that narrowing here; a sign-in it seeds carries
// the refresh token and the issuer to narrow at, as a real sign-in leaves them.
internal static class DepotHostScope
{
    private const string RefreshToken = "journey-depot-refresh";

    public static McpOAuthToken SignedIn(string bearer, string url) => new()
    {
        AccessToken = bearer,
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        Scope = "depot",
        ResourceUrl = $"{url}/mcp",
        RefreshToken = RefreshToken,
        AuthorizationServer = url,
    };

    // True when `context` was the narrowing itself: the issuer's metadata, or a refresh-token grant for depot.host alone.
    public static async Task<bool> TryAnswerAsync(HttpContext context, string bearer)
    {
        var request = context.Request;
        if (HttpMethods.IsGet(request.Method) && request.Path == "/.well-known/oauth-authorization-server")
        {
            await context.Response.WriteAsJsonAsync(new { issuer = $"{request.Scheme}://{request.Host}", token_endpoint = $"{request.Scheme}://{request.Host}/connect/token" });
            return true;
        }

        if (!HttpMethods.IsPost(request.Method) || request.Path != "/connect/token")
        {
            return false;
        }

        var form = await request.ReadFormAsync();
        if (form["grant_type"] != "refresh_token" || form["refresh_token"] != RefreshToken || form["scope"] != "depot.host")
        {
            context.Response.StatusCode = 400;
            return true;
        }

        await context.Response.WriteAsJsonAsync(new { access_token = bearer, token_type = "Bearer", expires_in = 3600 });
        return true;
    }
}
