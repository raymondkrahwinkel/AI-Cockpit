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

    [Fact]
    public async Task NothingWasEverRecorded_ReadsAsEmpty_RatherThanFailing()
    {
        var store = CreateStore();

        Assert.Empty((await store.TryLoadAsync(Pane))!);
        Assert.False(File.Exists(store.LogPath(Pane)));
    }

    [Fact]
    public async Task RecordedRows_ReadBackInOrder_IncludingADivider()
    {
        var store = CreateStore();
        var rows = new[]
        {
            Row("a", "UserText", "fix the layout bug"),
            Row("b", "Divider", "Context was full — a new conversation starts here"),
            new TranscriptSnapshotEntry("c", "ToolUse", "", "Bash", """{"command":"ls"}""", "tool-1", "file.txt", false, DateTimeOffset.Now),
        };

        foreach (var row in rows)
        {
            await store.AppendAsync(Pane, row);
        }

        Assert.Equal(rows, (await store.TryLoadAsync(Pane))!);
    }

    // THE ACCEPTANCE TEST for the shape: a row that changes after it was first recorded is one row on the way
    // back, at the position it first appeared, in its last version. This is what makes appending safe — without
    // last-version-wins the log replays the same row three times.
    [Fact]
    public async Task ARowRecordedAgainAfterItChanged_ReadsBackOnce_AtItsOriginalPositionAndInItsLastVersion()
    {
        var store = CreateStore();
        await store.AppendAsync(Pane, Row("a", "AssistantText", "Look"));
        await store.AppendAsync(Pane, Row("b", "UserText", "and then?"));
        await store.AppendAsync(Pane, Row("a", "AssistantText", "Looking at the layout now"));

        var loaded = (await store.TryLoadAsync(Pane))!;

        Assert.Equal(2, loaded.Count);
        Assert.Equal("a", loaded[0].Id);
        Assert.Equal("Looking at the layout now", loaded[0].Text);
        Assert.Equal("and then?", loaded[1].Text);
    }

    // The whole point of an append-only log: recording a changed row again must not rewrite what came before it.
    // Measured rather than reasoned about — the file only ever grows, and by the size of what was added.
    [Fact]
    public async Task RecordingARowAgain_AppendsIt_RatherThanRewritingTheLog()
    {
        var store = CreateStore();
        await store.AppendAsync(Pane, Row("a", "UserText", "first"));
        var afterFirst = new FileInfo(store.LogPath(Pane)).Length;

        await store.AppendAsync(Pane, Row("a", "UserText", "first, corrected"));

        Assert.True(
            new FileInfo(store.LogPath(Pane)).Length > afterFirst,
            "a second version must be appended after the first, not replace the file");
        Assert.Equal("first, corrected", Assert.Single((await store.TryLoadAsync(Pane))!).Text);
    }

    [Fact]
    public async Task EachPane_KeepsItsOwnLog_RatherThanSharingOne()
    {
        var store = CreateStore();
        await store.AppendAsync("pane-a", Row("a", "UserText", "for a"));
        await store.AppendAsync("pane-b", Row("b", "UserText", "for b"));

        Assert.Equal("for a", Assert.Single((await store.TryLoadAsync("pane-a"))!).Text);
        Assert.Equal("for b", Assert.Single((await store.TryLoadAsync("pane-b"))!).Text);
    }

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

    // A log written by a build that did not know a member yet, and one that knows a member this build does not:
    // both read rather than throwing away the row. That additive contract is what lets AC-1090's two unanswered
    // decisions (images, a pending permission) be filled in later without a migration.
    [Fact]
    public async Task ALineFromAnotherBuild_ReadsAsFarAsThisBuildUnderstandsIt()
    {
        var store = CreateStore();
        await File.WriteAllLinesAsync(
            store.LogPath(Pane),
            [
                """{"Id":"a","Kind":"UserText","Text":"older build","Timestamp":"2026-08-30T12:00:00+00:00"}""",
                """{"Id":"b","Kind":"UserText","Text":"newer build","IsResultError":false,"Timestamp":"2026-08-30T12:00:01+00:00","Images":[{"MediaType":"image/png"}]}""",
            ]);

        var loaded = (await store.TryLoadAsync(Pane))!;

        Assert.Equal(["older build", "newer build"], loaded.Select(entry => entry.Text));
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

    [Fact]
    public async Task Archiving_MovesThePanesLogToATimestampedPreviousGeneration()
    {
        var store = CreateStore();
        await store.AppendAsync(Pane, Row("a", "UserText", "before the crash"));

        await store.ArchiveAsync(Pane);

        Assert.False(File.Exists(store.LogPath(Pane)));
        var archived = Assert.Single(Directory.GetFiles(_tempDir, $"{Pane}.previous-*.jsonl"));
        Assert.Contains("before the crash", await File.ReadAllTextAsync(archived), StringComparison.Ordinal);
        Assert.Empty((await store.TryLoadAsync(Pane))!);
    }

    [Fact]
    public async Task ArchivingWithNothingEverRecorded_DoesNothing()
    {
        var store = CreateStore();

        await store.ArchiveAsync(Pane);

        Assert.Empty(Directory.GetFiles(_tempDir));
    }

    [Fact]
    public async Task ArchivingOnePane_LeavesAnotherPanesLogAlone()
    {
        var store = CreateStore();
        await store.AppendAsync("pane-a", Row("a", "UserText", "for a"));
        await store.AppendAsync("pane-b", Row("b", "UserText", "for b"));

        await store.ArchiveAsync("pane-a");

        Assert.Empty((await store.TryLoadAsync("pane-a"))!);
        Assert.Equal("for b", Assert.Single((await store.TryLoadAsync("pane-b"))!).Text);
    }

    [Fact]
    public async Task MoreThanThreeArchives_KeepsOnlyTheThreeNewest()
    {
        // Four pre-existing generations, named so a plain sort orders them oldest to newest — cheaper than
        // driving the clock to produce five real archives one second apart.
        foreach (var stamp in new[] { "20260101-000000", "20260102-000000", "20260103-000000", "20260104-000000" })
        {
            await File.WriteAllTextAsync(Path.Combine(_tempDir, $"{Pane}.previous-{stamp}.jsonl"), "");
        }
        var store = CreateStore();
        await store.AppendAsync(Pane, Row("a", "UserText", "newest"));

        await store.ArchiveAsync(Pane);

        var remaining = Directory.GetFiles(_tempDir, $"{Pane}.previous-*.jsonl");
        Assert.Equal(3, remaining.Length);
        Assert.DoesNotContain(remaining, path => path.Contains("20260101-000000", StringComparison.Ordinal));
        Assert.DoesNotContain(remaining, path => path.Contains("20260102-000000", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
