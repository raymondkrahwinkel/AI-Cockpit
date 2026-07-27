using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.Core.Tests;

/// <summary>
/// A loopback HTTP server on an OS-assigned port, for tests that need a request to travel over a real socket.
/// It answers every request, on every path, with the handler it was started with.
/// </summary>
/// <remarks>
/// Kestrel binds the port itself and only afterwards reports the address it got, so the port is held from the
/// moment it is knowable. The pattern this replaces — probe a port with a <c>TcpListener</c>, release it, then
/// hand the bare number to an <c>HttpListener</c> — leaves a window in which the OS can give that same port to
/// something else, and a loaded CI runner is exactly where that window gets hit.
/// </remarks>
internal sealed class LoopbackHttpServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private LoopbackHttpServer(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    /// <summary>The address the server is bound to, trailing slash included — e.g. <c>http://127.0.0.1:54321/</c>.</summary>
    public string BaseUrl { get; }

    public static async Task<LoopbackHttpServer> StartAsync(RequestDelegate handle)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        app.Run(handle);

        try
        {
            await app.StartAsync();

            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("Kestrel did not expose its bound addresses.");

            return new LoopbackHttpServer(app, $"{addresses.Addresses.First().TrimEnd('/')}/");
        }
        catch
        {
            // A server that never reached the caller can only be disposed here, and a suite that leaks one
            // Kestrel host per failed bind turns a single flake into a cascade.
            await app.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
