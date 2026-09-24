using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using Cockpit.Core.Abstractions.Events;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.BackendApi;
using Cockpit.Infrastructure.Events;
using Cockpit.Infrastructure.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Infrastructure.Tests.BackendApi;

public sealed class EventsEndpointTests
{
    private const string Bootstrap = "ck_bootstrapKeyForTheEventsEndpointTests012345678";
    private static readonly NodeCaller Operator = new("testtest", "", ConnectKeyCapability.Admin, "127.0.0.1", CancellationToken.None);

    [Fact]
    public async Task HeaderAndQueryCursorWriteAnSseFrame()
    {
        var log = new BackendEventLog();
        var first = log.Append("row", "pane-a", new { value = 1 });
        var second = log.Append("row", "pane-a", new { value = 2 });
        var services = new ServiceCollection().AddSingleton<IBackendEventLog>(log).BuildServiceProvider();

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
            .AddSingleton<TimeProvider>(clock)
            .BuildServiceProvider();
        var pipe = new Pipe();
        using var stop = new CancellationTokenSource();
        var context = new DefaultHttpContext();
        context.RequestAborted = stop.Token;
        context.Response.Body = pipe.Writer.AsStream();
        var stream = EventsEndpoint.StreamAsync(context, services);
        using var reader = new StreamReader(pipe.Reader.AsStream());
        var line = reader.ReadLineAsync();

        Assert.False(line.IsCompleted);
        clock.Advance();
        Assert.Equal(": ping", await line.WaitAsync(TimeSpan.FromSeconds(1)));
        stop.Cancel();
        await stream.WaitAsync(TimeSpan.FromSeconds(1));
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

    private static async Task<string> _FrameAsync(IServiceProvider services, string header, string query)
    {
        var pipe = new Pipe();
        using var stop = new CancellationTokenSource();
        var context = new DefaultHttpContext();
        context.RequestAborted = stop.Token;
        context.Request.Headers["Last-Event-ID"] = header;
        context.Request.QueryString = new QueryString($"?after={query}");
        context.Response.Body = pipe.Writer.AsStream();
        var stream = EventsEndpoint.StreamAsync(context, services);
        using var reader = new StreamReader(pipe.Reader.AsStream());
        var id = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(1));
        var kind = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(1));
        var data = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(1));
        stop.Cancel();
        await stream.WaitAsync(TimeSpan.FromSeconds(1));
        return string.Join("\n", id, kind, data);
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
                .AddSingleton(verifier)
                .BuildServiceProvider();
            _certificate = new NodeSelfSignedCertificate(Path.Combine(_directory, "cert.pfx"));
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(_certificate.Value)));
            _app = builder.Build();
            McpAuthMiddleware.Require(_app, new McpAuthKey(), new SessionMcpKeyring(), _ => ValueTask.FromResult(true), new NodeSharedSecret(), verifier);
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
