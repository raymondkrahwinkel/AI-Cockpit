using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;
using NSubstitute;

namespace Cockpit.Backend.Tests.Sessions;

// AC-1377: the rows a session forms, from the host alone — a fake runtime, a consumer that applies each event as the
// interface asks, and no view model. `seq` is asserted on order only; it is one counter for the whole backend.
public class SessionHostTranscriptTests
{
    // AC 1 of AC-1368: a prompt goes out, the answer comes back as an upsert with a seq and a version, and it is in the
    // store without any view having drawn it.
    [Fact]
    public async Task APromptsAnswer_ArrivesAsAnUpsert_AndIsInTheStoreWithoutAView()
    {
        var (host, runtime, store) = _Started();
        var upserts = new List<TranscriptRowUpsert>();
        host.RowUpserted += upserts.Add;

        await host.SubmitAsync(new QueuedPrompt("hello", []));
        runtime.EventAppended += Raise.Event<Action<SessionEvent>>(new AssistantTextCompleted { SessionId = "S1", Text = "hello back" });

        var answer = Assert.Single(upserts);
        Assert.Equal(("AssistantText", "hello back", 1), (answer.Row.Kind, answer.Row.Text, answer.Version));
        Assert.True(answer.Seq > 0);
        Assert.Equal([answer.Row], store.Appended);
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), assembly =>
            assembly.GetName().Name is { } name && (name.StartsWith("Avalonia", StringComparison.Ordinal) || name == "Cockpit.App"));
    }

    // A row that grows is one row: every delta is a new version under the same id, never a row of its own.
    [Fact]
    public void AStreamedReply_IsOneRow_WhoseVersionRisesWithEveryDelta()
    {
        var (host, runtime, _) = _Started();
        var upserts = new List<TranscriptRowUpsert>();
        host.RowUpserted += upserts.Add;

        _Stream(runtime, "one ", "two ", "three");

        Assert.Single(upserts.Select(upsert => upsert.Row.Id).Distinct());
        Assert.Equal(Enumerable.Range(1, upserts.Count), upserts.Select(upsert => upsert.Version));
        Assert.Equal(upserts.Select(upsert => upsert.Seq).Order(), upserts.Select(upsert => upsert.Seq));
        Assert.Equal("one two three", upserts[^1].Row.Text);
    }

    // The consumer's duty from `ISessionTranscript`, done the way a headless consumer does it: on the event's own thread.
    private static (SessionHost<QueuedPrompt> Host, ISessionRuntime Runtime, RecordingStore Store) _Started()
    {
        var runtime = Substitute.For<ISessionRuntime>();
        runtime.IsRunning.Returns(true);
        var manager = Substitute.For<ISessionManager>();
        manager.Create(Arg.Any<SessionProfile?>()).Returns(runtime);
        var store = new RecordingStore();

        var host = new SessionHost<QueuedPrompt>(() => "pane-a", manager, TimeProvider.System, transcriptStore: store);
        host.EventAppended += hostEvent => host.ApplyToTranscript(hostEvent.Event);
        host.Attach(new SessionProfile("work", new ClaudeConfig("/fake/.claude")));
        return (host, runtime, store);
    }

    private static void _Stream(ISessionRuntime runtime, params string[] deltas) =>
        Array.ForEach(deltas, text => runtime.EventAppended += Raise.Event<Action<SessionEvent>>(
            new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = text }));

    private sealed class RecordingStore : ISessionTranscriptStore
    {
        public List<TranscriptSnapshotEntry> Appended { get; } = [];

        public Task AppendAsync(string paneId, TranscriptSnapshotEntry entry, CancellationToken cancellationToken = default)
        {
            Appended.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TranscriptSnapshotEntry>?> TryLoadAsync(string paneId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TranscriptSnapshotEntry>?>([]);

        public Task ArchiveAsync(string paneId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
