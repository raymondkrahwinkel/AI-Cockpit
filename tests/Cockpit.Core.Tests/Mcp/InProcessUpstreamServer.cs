using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// A loopback HTTP server that answers whatever a test tells it to and remembers what it was sent. Stands in for the
/// OAuth-protected MCP server on the far side of <c>McpOAuthProxyForwarder</c> (AC-524) — what is under test there is
/// a reverse proxy, so the far side only has to be a real HTTP endpoint, not a real MCP one.
/// </summary>
internal sealed class InProcessUpstreamServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private InProcessUpstreamServer(WebApplication app, string url)
    {
        _app = app;
        Url = url;
    }

    /// <summary>The address to hand a server config as its <c>Url</c>.</summary>
    public string Url { get; }

    /// <summary>The <c>Authorization</c> header of the last request that arrived, or null if it carried none.</summary>
    public string? LastAuthorization { get; private set; }

    /// <summary>The method of the last request that arrived.</summary>
    public string? LastMethod { get; private set; }

    /// <summary>The body of the last request that arrived, read in full.</summary>
    public string LastBody { get; private set; } = string.Empty;

    /// <summary>The headers of the last request that arrived, so a test can prove an MCP header survived the hop.</summary>
    public Dictionary<string, string> LastHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<InProcessUpstreamServer> StartAsync(Func<HttpContext, InProcessUpstreamServer, Task> handler)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();

        var addresses = () => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        InProcessUpstreamServer? server = null;

        app.Run(async context =>
        {
            server!.LastAuthorization = context.Request.Headers.Authorization.ToString() is { Length: > 0 } authorization ? authorization : null;
            server.LastMethod = context.Request.Method;
            server.LastHeaders.Clear();
            foreach (var header in context.Request.Headers)
            {
                server.LastHeaders[header.Key] = header.Value.ToString();
            }

            using var reader = new StreamReader(context.Request.Body);
            server.LastBody = await reader.ReadToEndAsync().ConfigureAwait(false);

            await handler(context, server).ConfigureAwait(false);
        });

        await app.StartAsync().ConfigureAwait(false);
        server = new InProcessUpstreamServer(app, $"{addresses().TrimEnd('/')}/mcp");
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}
