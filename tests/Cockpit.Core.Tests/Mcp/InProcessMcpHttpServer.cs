using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// A real MCP server (ModelContextProtocol.AspNetCore over Kestrel, loopback/dynamic port) started in-process
/// for <see cref="McpToolProviderConnectAsyncTests"/> (#26) — no external process/network dependency, so the
/// parallel-connect proof exercises an actual MCP handshake instead of a mocked transport. An optional per-request
/// delay lets a test prove several servers connect concurrently rather than one after another.
/// </summary>
internal sealed class InProcessMcpHttpServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private InProcessMcpHttpServer(WebApplication app, string url)
    {
        _app = app;
        Url = url;
    }

    /// <summary>The server's <c>/mcp</c> endpoint URL, ready to use as an <see cref="Cockpit.Core.Mcp.McpServerConfig.Url"/>.</summary>
    public string Url { get; }

    public static async Task<InProcessMcpHttpServer> StartAsync<TTool>(TimeSpan? delay = null) where TTool : class
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddMcpServer().WithHttpTransport().WithTools<TTool>();
        builder.WebHost.UseKestrel();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();

        if (delay is { } d)
        {
            // Delays every request (including the initialize/tools-list handshake), so a test can time
            // several such servers to prove they were connected concurrently, not one after another.
            app.Use(async (context, next) =>
            {
                await Task.Delay(d);
                await next(context);
            });
        }

        app.MapMcp("/mcp");
        await app.StartAsync().ConfigureAwait(false);

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel did not expose its bound addresses.");
        var boundUrl = addresses.Addresses.First();

        return new InProcessMcpHttpServer(app, $"{boundUrl.TrimEnd('/')}/mcp");
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}
