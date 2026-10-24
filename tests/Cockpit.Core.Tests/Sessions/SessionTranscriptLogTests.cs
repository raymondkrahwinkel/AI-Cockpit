using System.Text.Json;
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
    public async Task RecordedRows_ReadBackInOrder_IncludingADivider()
    {
        var store = CreateStore();
        var rows = new[]
        {
            Row("a", "UserText", "fix the layout bug"),
            Row("b", "Divider", "Context was full — a new conversation starts here"),
            new TranscriptSnapshotEntry("c", "ToolUse", "", "Bash", """{"command":"ls"}""", "tool-1", "file.txt", false, DateTimeOffset.Now),
        };

        // AC-1438: what only the live view draws is never written, so the tool row's line keeps the members it always had.
        await store.AppendAsync(Pane, rows[0]);
        await store.AppendAsync(Pane, rows[1]);
        await store.AppendAsync(Pane, rows[2] with { IsPendingPermission = true, StartsReply = true, TruncatedFromChars = 9 });

        Assert.Equal(rows, (await store.TryLoadAsync(Pane))!);
        Assert.Equal(
            ["Id", "Kind", "Text", "ToolName", "InputJson", "ToolUseId", "ResultText", "IsResultError", "Timestamp", "IsFailedTurnRow", "IsPendingBrokerAnswer"],
            JsonDocument.Parse(File.ReadLines(store.LogPath(Pane)).Last()).RootElement.EnumerateObject().Select(member => member.Name));
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
    public static IEnumerable<object[]> LinesFromAnyBuild =>
    [
        [
            (Func<SessionTranscriptLog, Task>)(store => File.WriteAllTextAsync(store.LogPath(Pane), """{"Id":"a","Kind":"UserText","Text":"older build","Timestamp":"2026-08-30T12:00:00+00:00"}""")),
            new TranscriptSnapshotEntry("a", "UserText", "older build", null, null, null, null, false, new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero)),
        ],
        [
            (Func<SessionTranscriptLog, Task>)(store => File.WriteAllTextAsync(store.LogPath(Pane), """{"Id":"b","Kind":"UserText","Text":"newer build","IsResultError":false,"Timestamp":"2026-08-30T12:00:01+00:00","Images":[{"MediaType":"image/png"}]}""")),
            new TranscriptSnapshotEntry("b", "UserText", "newer build", null, null, null, null, false, new DateTimeOffset(2026, 8, 30, 12, 0, 1, TimeSpan.Zero)),
        ],
        [
            (Func<SessionTranscriptLog, Task>)(store => store.AppendAsync(Pane, ThreadedRow)),
            ThreadedRow,
        ],
    ];

    // AC-1616: a line from before threads has no projects, so it reads under General; a row in two threads keeps all
    // its thread members, or a restart would move the operator's conversation between threads.
    private static readonly TranscriptSnapshotEntry ThreadedRow =
        new("c", "UserText", "voor Depot de route, voor WayMark het token", null, null, null, null, false, new DateTimeOffset(2026, 8, 30, 12, 0, 2, TimeSpan.Zero))
        {
            ProjectIds = ["depot", "waymark"],
            ProjectSources = new Dictionary<string, ProjectSource> { ["depot"] = ProjectSource.Explicit, ["waymark"] = ProjectSource.Mentioned },
            ProjectParts = [new TranscriptProjectPart("depot", 0, 19), new TranscriptProjectPart("waymark", 21, 22)],
            TurnProjectIds = ["depot", "waymark"],
            SenderPaneId = "pane-7",
            SenderTitle = "DEP-422",
            MessageKind = "heads-up",
            SubjectPaneId = "pane-8",
        };

    // The store is internal, so the way each line reaches the disk travels as `object` and is cast back once.
    [Theory]
    [MemberData(nameof(LinesFromAnyBuild))]
    public async Task ALineFromAnyBuild_ReadsAsFarAsThisBuildUnderstandsIt(object written, TranscriptSnapshotEntry expected)
    {
        var store = CreateStore();
        await ((Func<SessionTranscriptLog, Task>)written)(store);

        var loaded = Assert.Single(await store.TryLoadAsync(Pane) ?? []);

        Assert.Equivalent(expected, loaded, strict: true);
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
