using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Core.Tests.Sessions;

/// <summary>
/// Cockpit's own copy of a pane's conversation (AC-1090, was AC-684's one-file-per-app assistant snapshot): what
/// the operator saw survives a round trip, a row that changed is not duplicated, panes do not read each other's
/// logs, and a machine that never recorded one — or a log it cannot make sense of — still starts.
/// </summary>
public class SessionTranscriptLogTests : IDisposable
{
    private const string Pane = "pane-1";

    private readonly string _tempDir;

    public SessionTranscriptLogTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    // AC-1151: a zero debounce window so these round-trip tests keep seeing an awaited `AppendAsync` land on disk
    // immediately — the debounce window itself has its own tests.
    private SessionTranscriptLog CreateStore() =>
        new(_tempDir, NullLogger<SessionTranscriptLog>.Instance, TimeSpan.Zero);

    private static TranscriptSnapshotEntry Row(string id, string kind, string text) =>
        new(id, kind, text, null, null, null, null, false, DateTimeOffset.Now);

    // Everything the grooming asked the format to carry beyond AC-684's eight fields, in one trip: without these a
    // restored sub-agent run is an empty chip, a failed turn is a grey line, and nobody can tell the tool call was
    // approved rather than run unasked.
    [Fact]
    public async Task ARowsSubAgentRowsPermissionErrorThreadAndBackgroundTask_AllSurviveTheRoundTrip()
    {
        var store = CreateStore();
        var row = new TranscriptSnapshotEntry("a", "ToolUse", "", "Task", """{"prompt":"go"}""", "tool-1", "done", false, DateTimeOffset.Now)
        {
            SubAgentRows = [Row("nested", "AssistantText", "reading the file")],
            PermissionDecision = "allow",
            ErrorKind = SessionErrorKind.RateLimited,
            RetryAfter = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero),
            IsFailedTurnRow = true,
            ReplyToId = "earlier",
            LatestReplyId = "later",
            BackgroundTaskId = "bg-7",
        };

        await store.AppendAsync(Pane, row);

        // Compared member by member rather than with record equality: `SubAgentRows` is a list, so `==` on the
        // record would pass on reference identity and quietly say nothing about what came back off disk.
        var loaded = Assert.Single((await store.TryLoadAsync(Pane))!);
        Assert.Equal(row with { SubAgentRows = null }, loaded with { SubAgentRows = null });
        Assert.Equal("reading the file", Assert.Single(loaded.SubAgentRows!).Text);
    }

    [Fact]
    public async Task ALineThatCannotBeParsed_IsSkipped_RatherThanLosingTheRowsAroundIt()
    {
        var store = CreateStore();
        await store.AppendAsync(Pane, Row("a", "UserText", "before"));
        await File.AppendAllTextAsync(store.LogPath(Pane), "not json" + Environment.NewLine);
        await store.AppendAsync(Pane, Row("b", "UserText", "after"));

        Assert.Equal(["before", "after"], (await store.TryLoadAsync(Pane))!.Select(entry => entry.Text));
    }

    // ── Rolling a log aside (AC-947, kept by AC-1090) ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
