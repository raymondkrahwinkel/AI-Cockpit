using System.Net;
using System.Text.Json;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugin.Depot.Notes.Live;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Cockpit.Plugin.Depot.Tests;

// AC-1897, layer 2: the live note's session against an in-process hub and a real Y.Doc in Jint.
public class DepotNoteLiveTests
{
    private static readonly DepotConnectionRegistration Connection = new("work", "Work", "https://work.example.com");
    private static readonly DepotNoteLiveRequest Note = new("work", "acme", "roadmap", "notes/plan.md");

    // Yjs v1: client 1 types "Hello" into the front matter text; client 2, which has seen that, adds " world".
    private const string Row = "AQEBAAQBC2Zyb250bWF0dGVyBUhlbGxvAA==";
    private const string Early = "AQECAIQBBAYgd29ybGQA";

    private const string Secret = "sk-live-bearer-0123456789";

    [Fact]
    public async Task Join_AppliesAnUpdateThatCameBeforeTheResult_AndDoesNotSendItBack()
    {
        var hub = new _Hub(async self =>
        {
            await self.Push(new DepotNoteEvent("update", ["doc-1", Convert.FromBase64String(Early)]));
            await Task.Yield();
            return _Result([new DepotNoteRow(1, Convert.FromBase64String(Row))]);
        });
        var live = new TaskCompletionSource();
        await using var session = new DepotNoteSession(
            Connection, Note, _ => Task.FromResult("user-1"), (_, _, onClosed) => hub.With(onClosed), clientId => new JintNoteRoom(clientId),
            status => { if (status.State == DepotNoteLiveState.Live) { live.TrySetResult(); } }, [TimeSpan.Zero]);

        session.Connect();
        await live.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var text = await session.Room.ReadAsync(CancellationToken.None);

        Assert.Equal("Hello world", text.Frontmatter);
        Assert.Empty(hub.Updates);
        Assert.Equal(DepotNoteSchema.Version, hub.Join?.Schema);
        Assert.False(hub.Join?.Save);
        Assert.Null(hub.Join?.Epoch);
    }

    [Theory]
    [InlineData("http-401")]
    [InlineData("hub-code")]
    public async Task SignInNeeded_IsPublishedOnce_WithoutRetry_AndNeverCarriesTheToken(string how)
    {
        var factoryCalls = 0;
        var statuses = new List<DepotNoteLiveStatus>();
        var logger = new _Log();
        Exception failure = how == "http-401"
            ? new HttpRequestException($"401 Authorization: Bearer {Secret}", null, HttpStatusCode.Unauthorized)
            : new HubException($"auth.session_expired: refused (Bearer {Secret})");
        await using var session = new DepotNoteSession(
            Connection, Note, _ => Task.FromResult("user-1"),
            (_, _, onClosed) =>
            {
                factoryCalls++;
                return new _Hub(_ => throw failure).With(onClosed);
            },
            clientId => new JintNoteRoom(clientId), statuses.Add, [TimeSpan.Zero], logger);

        session.Connect();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, factoryCalls);
        Assert.Equal([DepotNoteLiveState.Connecting, DepotNoteLiveState.SignInNeeded], statuses.Select(status => status.State));
        var published = JsonSerializer.Serialize(statuses, DepotChannel.Json);
        Assert.DoesNotContain(Secret, published);
        Assert.DoesNotContain("bearer", published, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authorization", published, StringComparison.OrdinalIgnoreCase);
        Assert.All(logger.Lines, line => Assert.DoesNotContain(Secret, line));

        // Signing in again is what starts the next attempt, and nothing else.
        session.Connect();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(2, factoryCalls);
    }

    private static DepotNoteJoinResult _Result(IReadOnlyList<DepotNoteRow> rows) =>
        new("doc-1", "editor", rows, Seed: false, AwaitingSeed: false, Epoch: "epoch-1", Stale: false, SchemaMismatch: false);

    private sealed class _Hub(Func<_Hub, Task<DepotNoteJoinResult>> join) : IDepotNotesHub
    {
        private Func<DepotNoteEvent, Task>? _handler;
        private Func<Exception?, Task>? _closed;

        public List<byte[]> Updates { get; } = [];

        public DepotNoteJoinRequest? Join { get; private set; }

        public _Hub With(Func<Exception?, Task> onClosed)
        {
            _closed = onClosed;
            return this;
        }

        // Like the real hub: an event goes to the handler registered when it arrives, and to nobody before that.
        public Task Push(DepotNoteEvent hubEvent) => _handler?.Invoke(hubEvent) ?? Task.CompletedTask;

        public void Subscribe(Func<DepotNoteEvent, Task> onEvent) => _handler = onEvent;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<DepotNoteJoinResult> JoinAsync(DepotNoteJoinRequest request, CancellationToken cancellationToken)
        {
            Join = request;
            return join(this);
        }

        public Task UpdateAsync(string documentId, byte[] update, CancellationToken cancellationToken)
        {
            Updates.Add(update);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => _closed is null ? ValueTask.CompletedTask : new ValueTask(_closed(null));
    }

    private sealed class _Log : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }
}
