using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cockpit.Plugin.Depot.Tests;

public sealed class DepotSignalRTests
{
    // Layer 2: auth. A real SignalR WebSocket upgrade must use the host's private bearer, without SSE fallback.
    [Fact]
    public async Task WebSocket_upgrade_uses_the_host_authorized_handler()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR();
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.Authorization != "Bearer test-host-only")
            {
                context.Response.StatusCode = 403;
                return;
            }

            await next(context);
        });
        app.MapHub<AuthHub>("/hub");
        await app.StartAsync(deadline.Token);
        var origin = app.Urls.Single();
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.AcquireAsync(Arg.Any<McpServerConfig>(), interactive: false, Arg.Any<CancellationToken>())
            .Returns(McpOAuthAccess.Authorized("test-host-only"));
        using var services = new ServiceCollection()
            .AddSingleton(coordinator)
            .AddSingleton<IPluginMcpProvider>(new OwnProvider(origin))
            .BuildServiceProvider();
        var host = new PluginBackendHost(
            "depot", "Depot", services, Substitute.For<IPluginStorage>(),
            Substitute.For<ICockpitSessionObserver>(), Substitute.For<ICockpitActions>(),
            new PluginDiagnostics(), typeof(OwnProvider));
        var requests = new List<HttpRequestMessage>();
        await using var connection = new HubConnectionBuilder()
            .WithUrl($"{origin}/hub", options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                DepotSignalR.Configure(options, () => new ObservingHandler(host.CreateAuthorizedHandler("Depot: Work"), requests));
            })
            .Build();

        await connection.StartAsync(deadline.Token);
        var result = await connection.InvokeAsync<string>("Echo", "connected", deadline.Token);

        Assert.Equal("connected", result);
        Assert.Contains(requests, request => request.Headers.Contains("Sec-WebSocket-Key"));
        Assert.All(requests, request => Assert.Null(request.Headers.Authorization));
        await connection.StopAsync(deadline.Token);
    }

    public sealed class AuthHub : Hub
    {
        public string Echo(string value) => value;
    }

    private sealed class OwnProvider(string origin) : IPluginMcpProvider
    {
        public IReadOnlyList<McpServerContribution> GetMcpServers() =>
            [new("Depot: Work", $"{origin}/mcp") { OAuthAuthority = origin }];
    }

    private sealed class ObservingHandler(HttpMessageHandler inner, List<HttpRequestMessage> requests) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requests.Add(request);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
