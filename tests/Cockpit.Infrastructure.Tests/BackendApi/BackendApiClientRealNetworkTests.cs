using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Cockpit.Core.Abstractions.Events;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.BackendApi;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.BackendApi;

public sealed class BackendApiClientRealNetworkTests
{
    private static readonly NodeCaller Operator = new(
        "testtest",
        "",
        ConnectKeyCapability.Admin,
        "127.0.0.1",
        CancellationToken.None);

    [Fact]
    public async Task WhoAmIAsync_UsesThePinnedCertificateAndReturnsTheKeyIdentity()
    {
        await using var server = await _Server.StartAsync(_LogMode.Wait);
        using var client = server.Client();

        var identity = await client.WhoAmIAsync();
        var strangeError = await Assert.ThrowsAsync<BackendApiException>(() =>
            client.GetAsync<JsonElement>("api/v1/strange-error"));
        using var wrongPin = new BackendApiClient(server.BaseAddress, server.Key, new string('0', 64), TimeProvider.System);
        await Assert.ThrowsAsync<HttpRequestException>(() => wrongPin.WhoAmIAsync());
        Assert.Throws<ArgumentException>(() =>
            new BackendApiClient(new Uri("http://127.0.0.1"), server.Key, server.Fingerprint, TimeProvider.System));

        Assert.Equal(server.KeyPrefix, identity.KeyPrefix);
        Assert.Equal("operate", identity.Capability);
        Assert.Equal((HttpStatusCode)StatusCodes.Status418ImATeapot, strangeError.Status);
        Assert.Equal(2, server.Requests);
    }

    [Fact]
    public async Task StreamEventsAsync_AfterAConnectionBreakResumesFromTheLastEventId()
    {
        await using var server = await _Server.StartAsync(_LogMode.BreakAfterThree);
        using var client = server.Client();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var events = await _ReadAsync(client.StreamEventsAsync(null, stop.Token), 5, stop.Token);

        Assert.Equal([1L, 2L, 3L, 4L, 5L], events.Select(evt => evt.Seq));
        Assert.Equal([-1L, 3L], server.Log.Cursors);
    }

    [Fact]
    public async Task StreamEventsAsync_AfterKeyRevocationThrowsOneUnauthorizedWithoutRetrying()
    {
        var clock = new _Clock();
        await using var server = await _Server.StartAsync(_LogMode.Wait);
        using var client = server.Client(clock);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var events = client.StreamEventsAsync(null, stop.Token).GetAsyncEnumerator(stop.Token);
        var next = events.MoveNextAsync().AsTask();
        await _UntilAsync(() => server.Log.Calls == 1, stop.Token);

        await server.RevokeAsync();
        await clock.Waiting.WaitAsync(stop.Token);
        clock.Advance();
        var exception = await Assert.ThrowsAsync<BackendApiException>(() => next);
        clock.Advance();

        Assert.Equal(HttpStatusCode.Unauthorized, exception.Status);
        Assert.Equal("invalid_token", exception.ErrorCode);
        Assert.Equal(1, server.UnauthorizedRequests);
        Assert.Equal(2, server.Requests);
        Assert.Equal(1, clock.TimerCount);
    }

    [Fact]
    public async Task StreamEventsAsync_AServiceUnavailableResponseWaitsForBackoffBeforeReconnecting()
    {
        var clock = new _Clock();
        await using var server = await _Server.StartAsync(_LogMode.ServiceUnavailableOnce);
        using var client = server.Client(clock);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var events = client.StreamEventsAsync(null, stop.Token).GetAsyncEnumerator(stop.Token);
        var next = events.MoveNextAsync().AsTask();

        await clock.Waiting.WaitAsync(stop.Token);
        Assert.Equal(1, server.Requests);
        Assert.Equal(0, server.Log.Calls);
        clock.Advance();
        await _UntilAsync(() => server.Log.Calls == 1, stop.Token);
        Assert.Equal(2, server.Requests);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
    }

    [Fact]
    public async Task StreamEventsAsync_HeartbeatCommentDoesNotProduceAnEvent()
    {
        var serverClock = new _Clock();
        await using var server = await _Server.StartAsync(_LogMode.Wait, serverClock);
        using var client = server.Client();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var events = client.StreamEventsAsync(null, stop.Token).GetAsyncEnumerator(stop.Token);
        var next = events.MoveNextAsync().AsTask();
        await _UntilAsync(() => server.Log.Calls == 1, stop.Token);

        serverClock.Advance();
        await Task.Delay(TimeSpan.FromMilliseconds(100), stop.Token);
        Assert.False(next.IsCompleted);
        server.Log.Publish(9, "reset");

        Assert.True(await next);
        Assert.Equal(9, events.Current.Seq);
        Assert.Equal("reset", events.Current.Kind);
    }

    private static async Task<IReadOnlyList<BackendEvent>> _ReadAsync(
        IAsyncEnumerable<BackendEvent> source,
        int count,
        CancellationToken cancellationToken)
    {
        var events = new List<BackendEvent>(count);
        await foreach (var evt in source.WithCancellation(cancellationToken))
        {
            events.Add(evt);
            if (events.Count == count)
            {
                break;
            }
        }

        return events;
    }

    private static async Task _UntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(10, cancellationToken);
        }
    }

    private static BackendEvent _Event(long seq) =>
        new(seq, "row", null, JsonSerializer.SerializeToElement(new { value = seq }));

    private enum _LogMode
    {
        Wait,
        BreakAfterThree,
        ServiceUnavailableOnce,
    }

    private sealed class _Log(_LogMode mode) : IBackendEventLog
    {
        private readonly Channel<BackendEvent> _events = Channel.CreateUnbounded<BackendEvent>();
        private int _calls;

        public int Calls => _calls;

        public ConcurrentQueue<long> Cursors { get; } = new();

        public long Append(string kind, string? paneId, object data, long? seq = null) => throw new NotSupportedException();

        public IAsyncEnumerable<BackendEvent> ReadFromAsync(long afterSeq, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            Cursors.Enqueue(afterSeq);
            return mode switch
            {
                _LogMode.BreakAfterThree => _BreakingAsync(call, cancellationToken),
                _ => _events.Reader.ReadAllAsync(cancellationToken),
            };
        }

        public void Publish(long seq, string kind) =>
            _events.Writer.TryWrite(_Event(seq) with { Kind = kind });

        private static async IAsyncEnumerable<BackendEvent> _BreakingAsync(
            int call,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var first = call == 1 ? 1 : 4;
            var last = call == 1 ? 3 : 5;
            for (var seq = first; seq <= last; seq++)
            {
                yield return _Event(seq);
            }

            if (call == 1)
            {
                throw new IOException("The test server broke the stream.");
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

    }

    private sealed class _Clock : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;
        private readonly TaskCompletionSource _waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int TimerCount { get; private set; }

        public Task Waiting => _waiting.Task;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            _state = state;
            TimerCount++;
            _waiting.TrySetResult();
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
        private const string Bootstrap = "ck_bootstrapKeyForBackendApiClientTests012345678";
        private readonly string _directory;
        private readonly NodeSelfSignedCertificate _certificate;
        private readonly ServiceProvider _services;
        private readonly WebApplication _app;
        private readonly ConnectKeyVerifier _verifier;
        private int _requests;
        private int _unauthorizedRequests;

        private _Server(
            string directory,
            NodeSelfSignedCertificate certificate,
            ServiceProvider services,
            WebApplication app,
            ConnectKeyVerifier verifier,
            _Log log,
            Uri baseAddress,
            string key,
            string keyPrefix)
        {
            _directory = directory;
            _certificate = certificate;
            _services = services;
            _app = app;
            _verifier = verifier;
            Log = log;
            BaseAddress = baseAddress;
            Key = key;
            KeyPrefix = keyPrefix;
        }

        public _Log Log { get; }

        public Uri BaseAddress { get; private set; }

        public string Key { get; }

        public string KeyPrefix { get; }

        public string Fingerprint => _certificate.Fingerprint;

        public int Requests => _requests;

        public int UnauthorizedRequests => _unauthorizedRequests;

        public static async Task<_Server> StartAsync(_LogMode mode, TimeProvider? serverTime = null)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"backend-client-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var certificate = new NodeSelfSignedCertificate(Path.Combine(directory, "certificate.pfx"));
            var audit = new NodeAccessAuditLog(Path.Combine(directory, "audit.jsonl"), NullLogger<NodeAccessAuditLog>.Instance);
            var environment = new Dictionary<string, string> { [ConnectKeyVerifier.BootstrapVariable] = Bootstrap };
            var verifier = new ConnectKeyVerifier(
                Path.Combine(directory, "cockpit.json"),
                name => environment.GetValueOrDefault(name),
                name => environment.Remove(name),
                TimeProvider.System,
                audit,
                NullLogger.Instance);
            await verifier.EnsureLoadedAsync();
            var issued = await verifier.IssueAsync("controller", ConnectKeyCapability.Operate, 30, Operator);
            var log = new _Log(mode);
            var collection = new ServiceCollection()
                .AddSingleton<IBackendEventLog>(log)
                .AddSingleton<ISessionRegistry>(new SessionRegistry())
                .AddSingleton(Substitute.For<INodePairingBroker>())
                .AddSingleton(verifier);
            if (serverTime is not null)
            {
                collection.AddSingleton(serverTime);
            }

            var services = collection.BuildServiceProvider();
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.ConfigureKestrel(options =>
                options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate.Value)));
            var app = builder.Build();
            var server = new _Server(directory, certificate, services, app, verifier, log, new Uri("https://127.0.0.1"), issued.Secret, issued.Key.Prefix);
            var returnedServiceUnavailable = 0;
            app.Use(async (context, next) =>
            {
                Interlocked.Increment(ref server._requests);
                if (mode == _LogMode.ServiceUnavailableOnce
                    && context.Request.Path == "/api/v1/events"
                    && Interlocked.Exchange(ref returnedServiceUnavailable, 1) == 0)
                {
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    await context.Response.WriteAsync("{\"error\":\"unavailable\",\"error_description\":\"Try again.\"}");
                    return;
                }

                await next();
                if (context.Response.StatusCode == StatusCodes.Status401Unauthorized)
                {
                    Interlocked.Increment(ref server._unauthorizedRequests);
                }
            });
            McpAuthMiddleware.Require(
                app,
                new McpAuthKey(),
                new SessionMcpKeyring(),
                _ => ValueTask.FromResult(true),
                new NodeSharedSecret(),
                verifier);
            app.MapGet("/api/v1/strange-error", () => Results.Text("[]", "application/json", statusCode: StatusCodes.Status418ImATeapot));
            BackendApiRoutes.Map(app, services);
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.Single()
                ?? throw new InvalidOperationException("Kestrel did not expose its HTTPS address.");
            server.BaseAddress = new Uri($"{address.TrimEnd('/')}/");
            return server;
        }

        public BackendApiClient Client(TimeProvider? time = null) =>
            new(BaseAddress, Key, _certificate.Fingerprint, time ?? TimeProvider.System);

        public async Task RevokeAsync() => await _verifier.RevokeAsync(KeyPrefix, Operator);

        public async ValueTask DisposeAsync()
        {
            await _app.DisposeAsync();
            await _services.DisposeAsync();
            _certificate.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
