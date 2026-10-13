using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Infrastructure.Tests.Sessions;

public sealed class ConversationIndexStoreTests : IDisposable
{
    private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"conversation-index-{Guid.NewGuid():N}");
    private string Path => System.IO.Path.Combine(_root, "conversations.jsonl");
    private ConversationIndexStore Store() => new(Path, NullLogger<ConversationIndexStore>.Instance);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private static ConversationRecord Record(string pane = "one", string title = "Jansen", string? project = "accounting", string? transcript = null, SessionConversationIdState state = SessionConversationIdState.Known) =>
        new(pane, title, project, "C:\\project", "Echo", "Claude", "conversation-1", state, new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero), "Done", null, false, false, transcript);

    [Fact]
    public async Task AppendedClosedConversation_SurvivesLastRecordWinsAndNoSessionStateCompactionTouchesIt()
    {
        var store = Store();
        await store.RecordAsync(Record());
        await store.RecordAsync(Record(title: "Closed Jansen") with { ClosedAt = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero) });

        var record = Assert.Single(await new ConversationIndexStore(Path, NullLogger<ConversationIndexStore>.Instance).LoadAsync());
        Assert.Equal("Closed Jansen", record.Title);
        Assert.NotNull(record.ClosedAt);
    }

    [Theory]
    [InlineData("Jansen", "accounting", 1)]
    [InlineData("transcript-hit", "accounting", 1)]
    [InlineData("Jansen", "other", 0)]
    public async Task SearchAsync_FindsMetadataAndTranscriptAndAppliesProjectFilter(string search, string project, int count)
    {
        Directory.CreateDirectory(_root);
        var transcript = System.IO.Path.Combine(_root, "one.jsonl");
        await File.WriteAllTextAsync(transcript, "hello transcript-hit world");
        var store = Store();
        await store.RecordAsync(Record(transcript: transcript));

        var results = await store.SearchAsync(search, project);

        Assert.Equal(count, results.Count);
        if (count > 0) Assert.Contains(search, Assert.Single(results).Fragment!, StringComparison.OrdinalIgnoreCase);
    }
}
