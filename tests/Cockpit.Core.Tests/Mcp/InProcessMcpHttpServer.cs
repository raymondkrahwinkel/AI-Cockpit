using System.Collections.Concurrent;
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
    private readonly ConcurrentQueue<(DateTimeOffset Start, DateTimeOffset End)> _requestWindows;

    private InProcessMcpHttpServer(WebApplication app, string url, ConcurrentQueue<(DateTimeOffset Start, DateTimeOffset End)> requestWindows)
    {
        _app = app;
        Url = url;
        _requestWindows = requestWindows;
    }

    /// <summary>The server's <c>/mcp</c> endpoint URL, ready to use as an <see cref="Cockpit.Core.Mcp.McpServerConfig.Url"/>.</summary>
    public string Url { get; }

    /// <summary>
    /// When started with a <c>delay</c>, the start/end of every request's artificial wait — one entry per request
    /// (initialize, tools/list, ...). Lets a test prove two servers' handshakes were genuinely in flight at the same
    /// moment by comparing windows directly, rather than inferring it from how long the whole connect took: a wall-
    /// clock ratio can be fooled by ordinary scheduling noise stretching one measurement more than the other, which
    /// is exactly what made <see cref="McpToolProviderConnectAsyncTests"/> flake on a busy runner.
    /// </summary>
    public IReadOnlyCollection<(DateTimeOffset Start, DateTimeOffset End)> RequestWindows => _requestWindows;

    public static async Task<InProcessMcpHttpServer> StartAsync<TTool>(TimeSpan? delay = null) where TTool : class
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddMcpServer().WithHttpTransport().WithTools<TTool>();
        builder.WebHost.UseKestrel();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        var requestWindows = new ConcurrentQueue<(DateTimeOffset Start, DateTimeOffset End)>();

        if (delay is { } d)
        {
            // Delays every request (including the initialize/tools-list handshake), so a test can time
            // several such servers to prove they were connected concurrently, not one after another.
            app.Use(async (context, next) =>
            {
                var start = DateTimeOffset.UtcNow;
                await Task.Delay(d);
                requestWindows.Enqueue((start, DateTimeOffset.UtcNow));
                await next(context);
            });
        }

        app.MapMcp("/mcp");
        await app.StartAsync().ConfigureAwait(false);

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel did not expose its bound addresses.");
        var boundUrl = addresses.Addresses.First();

        return new InProcessMcpHttpServer(app, $"{boundUrl.TrimEnd('/')}/mcp", requestWindows);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}
