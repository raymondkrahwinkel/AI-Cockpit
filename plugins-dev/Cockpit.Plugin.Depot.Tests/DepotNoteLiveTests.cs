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

    private const string Secret = "sk-live-bearer-0123456789";

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

    private sealed class _Hub(Func<_Hub, Task<DepotNoteJoinResult>> join) : IDepotNotesHub
    {
        private Func<Exception?, Task>? _closed;

        public _Hub With(Func<Exception?, Task> onClosed)
        {
            _closed = onClosed;
            return this;
        }

        public void Subscribe(Func<DepotNoteEvent, Task> onEvent)
        {
        }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<DepotNoteJoinResult> JoinAsync(DepotNoteJoinRequest request, CancellationToken cancellationToken) => join(this);

        public Task UpdateAsync(string documentId, byte[] update, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EditedAsync(string documentId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CompactAsync(string documentId, byte[] snapshot, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> RefillAsync(string documentId, CancellationToken cancellationToken) => Task.FromResult(false);

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
