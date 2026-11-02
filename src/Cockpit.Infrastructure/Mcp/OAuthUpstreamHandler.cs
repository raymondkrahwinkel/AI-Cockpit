namespace Cockpit.Infrastructure.Mcp;

// The connection pool behind every OAuth-protected MCP server (the loopback proxy and a plugin's authorized
// handler). A tunnel in front of the server may close connections that sat quiet; dropping them here first keeps a
// request from being sent over one the tunnel already closed.
internal static class OAuthUpstreamHandler
{
    public static HttpMessageHandler Create() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };
}
