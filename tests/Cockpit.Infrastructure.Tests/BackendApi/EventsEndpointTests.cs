using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Text.Json;
using Cockpit.Core.Abstractions.Events;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Mcp;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.BackendApi;
using Cockpit.Infrastructure.Events;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.BackendApi;

public sealed class EventsEndpointTests
{
    private const string Bootstrap = "ck_bootstrapKeyForTheEventsEndpointTests012345678";
    private static readonly NodeCaller Operator = new("testtest", "", ConnectKeyCapability.Admin, "127.0.0.1", CancellationToken.None, IssuedToOwner: true);

    [Fact]
    public async Task HeaderAndQueryCursorWriteAnSseFrame()
    {
        var log = new BackendEventLog();
        var first = log.Append("row", "pane-a", new { value = 1 }, "profile-a", "project-a");
        var second = log.Append("row", "pane-a", new { value = 2 }, "profile-a", "project-a");
        var sessions = new SessionRegistry();
        sessions.Register(_Session("pane-a", "profile-a", "project-a"));
        sessions.Unregister("pane-a");
        var services = new ServiceCollection()
            .AddSingleton<IBackendEventLog>(log)
            .AddSingleton<ISessionRegistry>(sessions)
            .AddSingleton(Substitute.For<INodePairingBroker>())
            .BuildServiceProvider();

        var headerFrame = await _FrameAsync(services, first.ToString(), "");
        var queryFrame = await _FrameAsync(services, "", first.ToString());

        Assert.Equal($"id: {second}\nevent: row\ndata: {{\"value\":2}}", headerFrame);
        Assert.Equal(headerFrame, queryFrame);
    }

    [Fact]
    public async Task HeartbeatWaitsForFifteenSecondsOnTheInjectedClock()
    {
        var clock = new _Clock();
        var services = new ServiceCollection()
            .AddSingleton<IBackendEventLog>(new BackendEventLog())
            .AddSingleton<ISessionRegistry>(new SessionRegistry())
            .AddSingleton(Substitute.For<INodePairingBroker>())
            .AddSingleton<TimeProvider>(clock)
            .BuildServiceProvider();
        var pipe = new Pipe();
        using var stop = new CancellationTokenSource();
        var context = new DefaultHttpContext();
        context.RequestAborted = stop.Token;
        context.Response.Body = pipe.Writer.AsStream();
        McpRequestContext.Set(null, Operator);
        var stream = EventsEndpoint.StreamAsync(context, services);
        using var reader = new StreamReader(pipe.Reader.AsStream());
        var line = reader.ReadLineAsync();

        Assert.False(line.IsCompleted);
        clock.Advance();
        Assert.Equal(": ping", await line.WaitAsync(TimeSpan.FromSeconds(1)));
        stop.Cancel();
        await stream.WaitAsync(TimeSpan.FromSeconds(1));
        McpRequestContext.Set(null);
    }

    [Theory]
    [InlineData("project-b", "profile-a")]
    [InlineData("project-a", "profile-b")]
    public async Task ScopedKeyReceivesItsSessionAndSkipsTheOther(string otherProject, string otherProfile)
    {
        var log = new BackendEventLog();
        var first = log.Append("row", "pane-b", new { value = "outside" }, otherProfile, otherProject);
        // AC-1388: a pane's live state is held to the same scope as its rows.
        log.Append("live-state", "pane-b", new { value = "outside" }, otherProfile, otherProject);
        var second = log.Append("row", "pane-a", new { value = "inside" }, "profile-a", "project-a");
        var sessions = new SessionRegistry();
        sessions.Register(_Session("pane-b", otherProfile, otherProject));
        sessions.Register(_Session("pane-a", "profile-a", "project-a"));
        sessions.Unregister("pane-b");
        sessions.Unregister("pane-a");
        var services = new ServiceCollection()
            .AddSingleton<IBackendEventLog>(log)
            .AddSingleton<ISessionRegistry>(sessions)
            .AddSingleton(Substitute.For<INodePairingBroker>())
            .BuildServiceProvider();
        var scope = new ConnectKeyScope
        {
            AllowAllProfiles = false,
            AllowedProfileLabels = ["profile-a"],
            AllowAllProjects = false,
            AllowedProjectIds = ["project-a"],
        };

        var frame = await _FrameAsync(services, (first - 1).ToString(), "", scope);

        Assert.Equal($"id: {second}\nevent: row\ndata: {{\"value\":\"inside\"}}", frame);
    }

    // AC-1547 (layer 2, event stream key boundary): a plugin's channel event reaches the stream through the bridge, named
    // by the plugin's folder, and only an admin key reads it, as only an admin key may call the plugin's channel.
    [Theory]
    [InlineData(true, ConnectKeyCapability.Admin, "plugin {\"folder\":\"schedules-folder\",\"name\":\"run-recorded\",\"payload\":{\"id\":\"run-1\"}} | row {\"value\":\"assistant\"}")]
    [InlineData(false, ConnectKeyCapability.Operate, "row {\"value\":\"session\"}")]
    public async Task EachKeyReceivesOnlyTheAssistantAndPluginEventsItMayRead(bool holdsAssistant, ConnectKeyCapability capability, string expected)
    {
        var log = new BackendEventLog();
        var hub = new PluginChannelHub(NullLogger<PluginChannelHub>.Instance);
        var plugins = new PluginManager(NullLogger<PluginManager>.Instance, new PluginDiagnostics(false));
        plugins.LoadAndConfigure(
            [new DiscoveredPlugin(@"C:\plugins\schedules-folder", "schedules-folder", new PluginManifest("schedules", "Schedules", "1.0.0", "Schedules.dll", 3, null, null, null, null), "hash", PluginLoadDecision.Load)],
            new ServiceCollection(),
            _ => Substitute.For<ICockpitPlugin>());
        var sessions = new SessionRegistry();
        sessions.RegisterAssistant(_Session("assistant-pane", "profile-a", "project-a"));
        var services = new ServiceCollection()
            .AddSingleton<IBackendEventLog>(log)
            .AddSingleton<ISessionRegistry>(sessions)
            .AddSingleton(Substitute.For<INodePairingBroker>())
            .AddSingleton(plugins)
            .BuildServiceProvider();
        var bridge = new PluginEventsBridge(hub, log, services);
        await bridge.StartAsync(CancellationToken.None);
        using var admin = bridge.Listen();
        hub.Publish("schedules", "run-recorded", JsonDocument.Parse("""{"id":"run-1"}""").RootElement);
        var first = log.LastSeq;
        log.Append("row", "assistant-pane", new { value = "assistant" }, "profile-a", "project-a");
        log.Append("row", "pane-a", new { value = "session" }, "profile-a", "project-a");

        var frames = await _FramesAsync(services, (first - 1).ToString(), holdsAssistant, capability, holdsAssistant ? 2 : 1);

        Assert.Equal(expected, frames);
    }

    [Fact]
    public async Task RevokingOneConnectKeyClosesOnlyItsHttpsStream()
    {
        await using var server = new _Server();
        var verifier = await server.StartAsync();
        var first = await verifier.IssueAsync("first", ConnectKeyCapability.Operate, 30, Operator);
        var second = await verifier.IssueAsync("second", ConnectKeyCapability.Operate, 30, Operator);
        using var revokedResponse = await server.OpenAsync(first.Secret);
        using var survivorResponse = await server.OpenAsync(second.Secret);
        using var revokedStream = await revokedResponse.Content.ReadAsStreamAsync();
        using var survivorStream = await survivorResponse.Content.ReadAsStreamAsync();
        var revokedRead = revokedStream.ReadAsync(new byte[1]).AsTask();
        using var survivorStop = new CancellationTokenSource();
        var survivorRead = survivorStream.ReadAsync(new byte[1], survivorStop.Token).AsTask();

        await verifier.RevokeAsync(first.Key.Prefix, Operator);

        Assert.Same(revokedRead, await Task.WhenAny(revokedRead, Task.Delay(TimeSpan.FromSeconds(1))));
        Assert.NotSame(survivorRead, await Task.WhenAny(survivorRead, Task.Delay(TimeSpan.FromMilliseconds(200))));
        survivorStop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await survivorRead);
    }

    // The next `count` frames as "kind data", joined by " | ".
    private static async Task<string> _FramesAsync(IServiceProvider services, string header, bool holdsAssistant, ConnectKeyCapability capability, int count)
    {
        var pipe = new Pipe();
        using var stop = new CancellationTokenSource();
        var context = new DefaultHttpContext();
        context.RequestAborted = stop.Token;
        context.Request.Headers["Last-Event-ID"] = header;
        context.Response.Body = pipe.Writer.AsStream();
        McpRequestContext.Set(null, Operator with { HoldsAssistant = holdsAssistant, Capability = capability });
        var stream = EventsEndpoint.StreamAsync(context, services);
        using var reader = new StreamReader(pipe.Reader.AsStream());
        List<string> frames = [];
        while (frames.Count < count)
        {
            await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(1));
            var kind = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(1));
            var data = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(1));
            await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(1));
            frames.Add($"{kind?["event: ".Length..]} {data?["data: ".Length..]}");
        }

        stop.Cancel();
        await stream.WaitAsync(TimeSpan.FromSeconds(1));
        McpRequestContext.Set(null);
        return string.Join(" | ", frames);
    }

    private static async Task<string> _FrameAsync(IServiceProvider services, string header, string query, ConnectKeyScope? scope = null, bool holdsAssistant = false)
    {
        var pipe = new Pipe();
        using var stop = new CancellationTokenSource();
        var context = new DefaultHttpContext();
        context.RequestAborted = stop.Token;
        context.Request.Headers["Last-Event-ID"] = header;
        context.Request.QueryString = new QueryString($"?after={query}");
        context.Response.Body = pipe.Writer.AsStream();
        McpRequestContext.Set(null, Operator with { Scope = scope, HoldsAssistant = holdsAssistant });
        var stream = EventsEndpoint.StreamAsync(context, services);
        using var reader = new StreamReader(pipe.Reader.AsStream());
        var id = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(1));
        var kind = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(1));
        var data = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(1));
        stop.Cancel();
        await stream.WaitAsync(TimeSpan.FromSeconds(1));
        McpRequestContext.Set(null);
        return string.Join("\n", id, kind, data);
    }

    private static ISessionHandle _Session(string paneId, string profile, string project)
    {
        var session = Substitute.For<ISessionHandle>();
        session.PaneId.Returns(paneId);
        session.ActiveProfileLabel.Returns(profile);
        session.ProjectId.Returns(project);
        return session;
    }

    private sealed class _Clock : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            _state = state;
            return new _Timer();
        }

        public void Advance() => _callback?.Invoke(_state);

        private sealed class _Timer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class _Server : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"events-endpoint-{Guid.NewGuid():N}");
        private readonly HttpClient _http = new(new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        });
        private WebApplication? _app;
        private NodeSelfSignedCertificate? _certificate;
        private string _base = "";

        public async Task<ConnectKeyVerifier> StartAsync()
        {
            Directory.CreateDirectory(_directory);
            var audit = new NodeAccessAuditLog(Path.Combine(_directory, "audit.jsonl"), NullLogger<NodeAccessAuditLog>.Instance);
            var environment = new Dictionary<string, string> { [ConnectKeyVerifier.BootstrapVariable] = Bootstrap };
            var verifier = new ConnectKeyVerifier(Path.Combine(_directory, "cockpit.json"), name => environment.GetValueOrDefault(name), name => environment.Remove(name), TimeProvider.System, audit, NullLogger.Instance);
            await verifier.EnsureLoadedAsync();
            var services = new ServiceCollection()
                .AddSingleton<IBackendEventLog>(new BackendEventLog())
                .AddSingleton<ISessionRegistry>(new SessionRegistry())
                .AddSingleton(Substitute.For<INodePairingBroker>())
                .AddSingleton(verifier)
                .AddSingleton<IConnectKeyAdministration>(verifier)
                .BuildServiceProvider();
            _certificate = new NodeSelfSignedCertificate(Path.Combine(_directory, "cert.pfx"));
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(_certificate.Value)));
            _app = builder.Build();
            McpAuthMiddleware.Require(_app, new McpAuthKey(), new SessionMcpKeyring(), _ => ValueTask.FromResult(true), verifier);
            BackendApiRoutes.Map(_app, services);
            await _app.StartAsync();
            _base = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.Single()
                ?? throw new InvalidOperationException("No HTTPS address.");
            return verifier;
        }

        public async Task<HttpResponseMessage> OpenAsync(string secret)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _base + "/api/v1/events");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        }

        public async ValueTask DisposeAsync()
        {
            if (_app is not null)
            {
                await _app.DisposeAsync();
            }

            _http.Dispose();
            _certificate?.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
