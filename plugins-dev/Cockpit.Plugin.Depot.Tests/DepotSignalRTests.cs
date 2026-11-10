using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugin.Depot.Events;
using Cockpit.Plugin.Depot.Mobile;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using System.Buffers.Text;
using System.Security.Cryptography;

namespace Cockpit.Plugin.Depot.Tests;

public sealed class DepotSignalRTests
{
    // B: signing in again must restore the relay without losing the saved phone or replacing its key.
    [Fact]
    [Trait("Category", "B")]
    public async Task Authorized_relay_recovers_after_sign_in_without_losing_the_phone_key()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR();
        var presence = new RelayPresence();
        builder.Services.AddSingleton(presence);
        await using var app = builder.Build();
        app.Use(_RequireBearerAsync);
        app.MapHub<AuthHub>("/hub");
        app.MapHub<AuthHub>("/hubs/relay");
        app.MapGet("/cockpit/me", () => new { userId = "user" });
        await app.StartAsync(deadline.Token);
        var origin = app.Urls.Single();
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        var signedIn = true;
        coordinator.AcquireAsync(Arg.Any<McpServerConfig>(), interactive: false, Arg.Any<CancellationToken>())
            .Returns(_ => signedIn ? McpOAuthAccess.Authorized("test-host-only") : McpOAuthAccess.AuthorizationRequired);
        using var services = new ServiceCollection()
            .AddSingleton(coordinator)
            .AddSingleton(new PluginMcpProviderRegistry(new OwnProvider(origin)))
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

        var registration = new DepotConnectionRegistration("work", "Work", origin);
        var storage = new FakePluginStorage();
        var keys = new MobileKeyStore(storage);
        Assert.True(keys.TryGetCockpitKey(registration.Id, out var originalKey));
        byte[] publicKey;
        using (originalKey) publicKey = MobileKeyStore.PublicKey(originalKey!);
        using var phoneKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        keys.AddPhone(registration.Id, MobileKeyStore.PublicKey(phoneKey));
        storage.Set("mobilePairedPhones-work", new List<MobilePairedPhone>
        {
            new("phone", "Phone", Base64Url.EncodeToString(MobileKeyStore.PublicKey(phoneKey)), DateTimeOffset.UnixEpoch),
        });
        var eventHub = new EventHub();
        await using var events = new DepotEvents(() => [registration],
            _ => host.CreateAuthorizedHandler(registration.McpServerName), (_, _, _, _, _) => eventHub);
        await using var pairing = new MobilePairing(() => [registration],
            _ => host.CreateAuthorizedHandler(registration.McpServerName),
            _ => host.CreateAuthorizedHandler(registration.McpServerName), storage, events, _ => null);

        pairing.Start();
        await presence.Connected.Task.WaitAsync(deadline.Token);
        Assert.Equal("connected", await connection.InvokeAsync<string>("Open", deadline.Token));

        signedIn = false;
        await pairing.ResetAsync(registration.McpServerName);
        await presence.Disconnected.Task.WaitAsync(deadline.Token);
        Assert.Equal("unknown_target", await connection.InvokeAsync<string>("Open", deadline.Token));

        presence.Connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        signedIn = true;
        await pairing.ResetAsync(registration.McpServerName);
        await presence.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5), deadline.Token);
        Assert.Equal("connected", await connection.InvokeAsync<string>("Open", deadline.Token));
        Assert.Single((await pairing.PhonesAsync(registration.Id, false, deadline.Token)).Phones);
        Assert.True(keys.IsPaired(registration.Id, MobileKeyStore.PublicKey(phoneKey)));
        Assert.True(keys.TryGetCockpitKey(registration.Id, out var restoredKey));
        using (restoredKey) Assert.Equal(publicKey, MobileKeyStore.PublicKey(restoredKey!));
        await connection.StopAsync(deadline.Token);
    }

    public sealed class AuthHub(RelayPresence presence) : Hub
    {
        public string Echo(string value) => value;

        public string Open() => presence.Connection is null ? "unknown_target" : "connected";

        public override Task OnConnectedAsync()
        {
            if (Context.GetHttpContext()?.Request.Path == "/hubs/relay")
            {
                presence.Connection = Context.ConnectionId;
                presence.Connected.TrySetResult();
            }
            return Task.CompletedTask;
        }

        public override Task OnDisconnectedAsync(Exception? exception)
        {
            if (presence.Connection == Context.ConnectionId)
            {
                presence.Connection = null;
                presence.Disconnected.TrySetResult();
            }
            return Task.CompletedTask;
        }
    }

    public sealed class RelayPresence
    {
        public string? Connection { get; set; }
        public TaskCompletionSource Connected { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class EventHub : IDepotEventHub
    {
        public bool IsConnected => true;
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SubscribeAsync(string topic, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task HeartbeatAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task _RequireBearerAsync(HttpContext context, RequestDelegate next)
    {
        if (context.Request.Headers.Authorization != "Bearer test-host-only")
        {
            context.Response.StatusCode = 403;
            return;
        }

        await next(context);
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
