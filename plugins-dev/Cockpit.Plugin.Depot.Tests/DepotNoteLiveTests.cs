using System.Net;
using System.Text.Json;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Files;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugin.Depot.Notes.Live;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Files;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cockpit.Plugin.Depot.Tests;

// AC-1897, layer 2: the live note's session against an in-process hub and a real Y.Doc in Jint.
public class DepotNoteLiveTests
{
    private static readonly DepotConnectionRegistration Connection = new("work", "Work", "https://work.example.com");
    private static readonly DepotNoteLiveRequest Note = new("work", "acme", "roadmap", "notes/plan.md");

    private const string Secret = "sk-live-bearer-0123456789";

    // AC-1901: the two ways a sign-in ends (an HTTP 401 and the hub's code) in one test, every assert kept, instead of a theory that
    // branched on its parameter.
    [Fact]
    public async Task SignInNeeded_IsPublishedOnce_WithoutRetry_AndNeverCarriesTheToken()
    {
        await _SignInNeededOnceAsync(new HttpRequestException($"401 Authorization: Bearer {Secret}", null, HttpStatusCode.Unauthorized));
        await _SignInNeededOnceAsync(new HubException($"auth.session_expired: refused (Bearer {Secret})"));
        await _StaleEpochDoesNotSendTheOfflineRoomAsync();
    }

    // AC-1903, layer 2: a stale epoch fences the old Jint room before its missing diff reaches the hub.
    private static async Task _StaleEpochDoesNotSendTheOfflineRoomAsync()
    {
        var reconnecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<DepotNoteJoinResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var joined = new DepotNoteJoinResult("doc-1", "editor", [], true, false, "epoch-old", false, false, Markdown: "# Original\n");
        var first = new _Hub(_ => Task.FromResult(joined));
        var second = new _Hub(_ =>
        {
            reconnecting.TrySetResult();
            return answer.Task;
        }) { RefuseUpdates = true };
        var hubs = new Queue<_Hub>([first, second]);
        await using var session = new DepotNoteSession(Connection, Note, _ => Task.FromResult(new DepotNoteUser("user-1")),
            (_, _, closed) => hubs.Dequeue().With(closed), clientId => new JintNoteRoom(clientId), _ => { }, [TimeSpan.Zero]);
        session.Connect();
        var snapshot = await session.ReadAsync(CancellationToken.None);
        await first.DropAsync();
        await reconnecting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await session.EditAsync("offline", snapshot.Rev, [new(0, 0, "MINE\n")], CancellationToken.None);
        answer.SetResult(joined with { Epoch = "epoch-new", Stale = true });
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(second.Updates);
        Assert.Equal("epoch-old", Assert.Single(second.Joins).Epoch);
        Assert.True(session.Status.RequiresRejoin);
        Assert.StartsWith("MINE\n", (await session.ReadAsync(CancellationToken.None)).Text);
    }

    private static async Task _SignInNeededOnceAsync(Exception failure)
    {
        var factoryCalls = 0;
        var statuses = new List<DepotNoteLiveStatus>();
        var logger = new _Log();
        await using var notes = new DepotNoteLive(
            () => [Connection],
            _ => new DepotBrowserModelTests.StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"userId\":\"user-1\"}") }),
            (_, _, onClosed) =>
            {
                factoryCalls++;
                return new _Hub(_ => throw failure).With(onClosed);
            },
            clientId => new JintNoteRoom(clientId), statuses.Add, logger);

        var session = notes.Open(Note);
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, factoryCalls);
        Assert.Equal([DepotNoteLiveState.Connecting, DepotNoteLiveState.SignInNeeded], statuses.Select(status => status.State));
        var published = JsonSerializer.Serialize(statuses, DepotChannel.Json);
        Assert.DoesNotContain(Secret, published);
        Assert.DoesNotContain("bearer", published, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authorization", published, StringComparison.OrdinalIgnoreCase);
        Assert.All(logger.Lines, line => Assert.DoesNotContain(Secret, line));

        // Signing in again is what starts the next attempt, and nothing else.
        var seen = new List<LiveFileStatus>();
        session.StatusChanged += (_, status) => seen.Add(status);
        Assert.Same(session, notes.Open(Note));
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(2, factoryCalls);
        Assert.Equal(LiveFileState.SignInNeeded, seen[^1].State);

        // AC-1580: the editor tab takes over the session that is open for its note, and any other file is a snapshot.
        var source = (ILiveFileSource)new DepotFileSourceProvider(Substitute.For<ICockpitHost>(), () => [Connection], () => notes).Open("work");
        Assert.Same(session, await source.OpenLiveAsync(DepotFileRefs.Create("work", "acme/roadmap", "notes/plan.md"), CancellationToken.None));
        var refused = await Assert.ThrowsAsync<FileSourceException>(() => source.OpenLiveAsync(DepotFileRefs.Create("work", "acme/roadmap", "notes/other.md"), CancellationToken.None));
        Assert.Equal(FileSourceErrorKind.Unsupported, refused.Kind);
        await session.DisposeAsync();
        Assert.NotSame(session, notes.Open(Note));
    }

    private sealed class _Hub(Func<_Hub, Task<DepotNoteJoinResult>> join) : IDepotNotesHub
    {
        private Func<Exception?, Task>? _closed;
        public List<byte[]> Updates { get; } = [];
        public List<DepotNoteJoinRequest> Joins { get; } = [];
        public bool RefuseUpdates { get; init; }
        public Task DropAsync() => _closed?.Invoke(null) ?? Task.CompletedTask;

        public _Hub With(Func<Exception?, Task> onClosed)
        {
            _closed = onClosed;
            return this;
        }

        public void Subscribe(Func<DepotNoteEvent, Task> onEvent)
        {
        }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<DepotNoteJoinResult> JoinAsync(DepotNoteJoinRequest request, CancellationToken cancellationToken)
        {
            Joins.Add(request);
            return join(this);
        }

        public Task UpdateAsync(string documentId, byte[] update, CancellationToken cancellationToken)
        {
            Updates.Add(update);
            if (RefuseUpdates)
            {
                throw new HubException("notes.unavailable: refused");
            }
            return Task.CompletedTask;
        }

        public Task EditedAsync(string documentId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CompactAsync(string documentId, byte[] snapshot, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> RefillAsync(string documentId, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task PresenceAsync(string documentId, byte[] awareness, CancellationToken cancellationToken) => Task.CompletedTask;

        public string? ConnectionId => null;

        public Task<DepotNoteText?> TextAsync(string documentId, string? checksum, CancellationToken cancellationToken) => Task.FromResult<DepotNoteText?>(null);

        public Task MergedAsync(string documentId, string checksum, int[] blocks, int conflicts, CancellationToken cancellationToken) => Task.CompletedTask;

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
