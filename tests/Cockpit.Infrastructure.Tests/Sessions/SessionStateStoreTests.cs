using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Infrastructure.Tests.Sessions;

/// <summary>
/// AC-409's whole reason to exist is a session-state write that survives a crash: unlike the audit trails, this
/// store's write path never buffers and never depends on a clean shutdown, so what is on disk right after
/// <c>RecordAsync</c> is exactly what a crash the next instant would leave behind. These tests hold that, plus the
/// round-trip, the "last record per pane wins" read, the half-written-last-line tolerance and compaction — using
/// xunit's own <c>Assert</c> throughout (FluentAssertions is not used in this codebase's new tests).
/// </summary>
public sealed class SessionStateStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"session-state-{Guid.NewGuid():N}.jsonl");

    private SessionStateStore CreateStore() => new(_path, NullLogger<SessionStateStore>.Instance);

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    private static SessionStateRecord CreateRecord(
        string paneId = "pane-1",
        string? profileId = "personal",
        string? providerId = "ClaudeCli",
        string? conversationId = "conv-abc",
        SessionConversationIdState conversationState = SessionConversationIdState.Known,
        string? workingDirectory = "/home/raymond/project",
        string? worktreePath = "/home/raymond/.config/Cockpit/worktrees/project/cockpit-project-1",
        string? worktreeBranch = "cockpit/project-1",
        string? permissionMode = "acceptEdits",
        DateTimeOffset? recordedAt = null) =>
        new(paneId, profileId, providerId, conversationId, conversationState, workingDirectory, worktreePath, worktreeBranch, permissionMode, recordedAt ?? DateTimeOffset.UtcNow);

    [Fact]
    public async Task RecordAsync_ThenLoadAsync_RoundTripsEveryField()
    {
        var store = CreateStore();
        var record = CreateRecord(recordedAt: new DateTimeOffset(2026, 7, 29, 10, 15, 0, TimeSpan.Zero));

        await store.RecordAsync(record);
        var loaded = await store.LoadAsync();

        var single = Assert.Single(loaded);
        Assert.Equal(record.PaneId, single.PaneId);
        Assert.Equal(record.ProfileId, single.ProfileId);
        Assert.Equal(record.ProviderId, single.ProviderId);
        Assert.Equal(record.ConversationId, single.ConversationId);
        Assert.Equal(record.ConversationState, single.ConversationState);
        Assert.Equal(record.WorkingDirectory, single.WorkingDirectory);
        Assert.Equal(record.WorktreePath, single.WorktreePath);
        Assert.Equal(record.WorktreeBranch, single.WorktreeBranch);
        Assert.Equal(record.PermissionMode, single.PermissionMode);
        Assert.Equal(record.RecordedAt, single.RecordedAt);
    }

    [Fact]
    public async Task LoadAsync_MultipleRecordsForOnePane_ReturnsOnlyTheLatest()
    {
        var store = CreateStore();
        await store.RecordAsync(CreateRecord(conversationId: "conv-1", permissionMode: "default"));
        await store.RecordAsync(CreateRecord(conversationId: "conv-2", permissionMode: "acceptEdits"));
        await store.RecordAsync(CreateRecord(conversationId: "conv-3", permissionMode: "bypassPermissions"));

        var loaded = await store.LoadAsync();

        var single = Assert.Single(loaded);
        Assert.Equal("conv-3", single.ConversationId);
        Assert.Equal("bypassPermissions", single.PermissionMode);
    }

    [Fact]
    public async Task LoadAsync_RecordsForDifferentPanes_KeepsTheLatestPerPane()
    {
        var store = CreateStore();
        await store.RecordAsync(CreateRecord(paneId: "pane-a", conversationId: "a-1"));
        await store.RecordAsync(CreateRecord(paneId: "pane-b", conversationId: "b-1"));
        await store.RecordAsync(CreateRecord(paneId: "pane-a", conversationId: "a-2"));

        var loaded = await store.LoadAsync();

        Assert.Equal(2, loaded.Count);
        Assert.Equal("a-2", loaded.Single(r => r.PaneId == "pane-a").ConversationId);
        Assert.Equal("b-1", loaded.Single(r => r.PaneId == "pane-b").ConversationId);
    }

    /// <summary>
    /// Criterion 3 (AC-409): the write has to be on disk the instant <c>RecordAsync</c> returns, not buffered until
    /// something later flushes or disposes it — proven with a *second*, independently constructed store reading the
    /// same file, so this cannot pass merely because the first store cached the record in memory.
    /// </summary>
    [Fact]
    public async Task RecordAsync_IsVisibleToASeparateStoreInstanceImmediately_WithNoDisposeOrShutdown()
    {
        var writer = CreateStore();

        await writer.RecordAsync(CreateRecord(paneId: "pane-live", conversationId: "conv-live"));

        // A fresh instance, as a second reader (or the next process) would construct — never told about `writer`,
        // and `writer` itself is never disposed, flushed at shutdown, or otherwise finalized.
        var reader = CreateStore();
        var loaded = await reader.LoadAsync();

        var single = Assert.Single(loaded);
        Assert.Equal("pane-live", single.PaneId);
        Assert.Equal("conv-live", single.ConversationId);
    }

    /// <summary>
    /// Criterion 4 (AC-409) — the reason this ticket exists: a session that ends without a graceful shutdown (a
    /// crash, a killed process) must still leave every write it made behind. Simulated by never calling any
    /// teardown on the store that wrote — there is none to call, RecordAsync is the only write path — and reading
    /// the file back through a brand-new instance, the way a restarted cockpit would.
    /// </summary>
    [Fact]
    public async Task AfterSeveralAppendsWithNoGracefulShutdown_ARestartReadsBackTheLatestStateForEveryPane()
    {
        var store = CreateStore();

        // A session's lifetime in miniature: started, its conversation id reported, its permission mode switched —
        // three separate writes, as SessionStateRecorder would make them, on two different panes.
        await store.RecordAsync(CreateRecord(paneId: "pane-1", conversationId: null, conversationState: SessionConversationIdState.Unknown, permissionMode: "default"));
        await store.RecordAsync(CreateRecord(paneId: "pane-2", conversationId: "conv-2", permissionMode: "default"));
        await store.RecordAsync(CreateRecord(paneId: "pane-1", conversationId: "conv-1", conversationState: SessionConversationIdState.Known, permissionMode: "acceptEdits"));

        // `store` is simply abandoned here — no Dispose, no flush, no "closing" call of any kind — before a fresh
        // instance stands in for the restarted process.
        var restarted = new SessionStateStore(_path, NullLogger<SessionStateStore>.Instance);
        var loaded = await restarted.LoadAsync();

        Assert.Equal(2, loaded.Count);
        var pane1 = loaded.Single(r => r.PaneId == "pane-1");
        Assert.Equal("conv-1", pane1.ConversationId);
        Assert.Equal(SessionConversationIdState.Known, pane1.ConversationState);
        Assert.Equal("acceptEdits", pane1.PermissionMode);
        var pane2 = loaded.Single(r => r.PaneId == "pane-2");
        Assert.Equal("conv-2", pane2.ConversationId);
    }

    [Fact]
    public async Task LoadAsync_HalfWrittenLastLine_SkipsItAndReturnsTheEarlierRecords()
    {
        var store = CreateStore();
        await store.RecordAsync(CreateRecord(paneId: "pane-good", conversationId: "conv-good"));

        // A crash mid-append: the line has no closing brace and no trailing newline, exactly what a write cut off
        // partway through would leave.
        await File.AppendAllTextAsync(_path, """{"PaneId":"pane-bad","ProfileId":"pers""");

        var loaded = await store.LoadAsync();

        var single = Assert.Single(loaded);
        Assert.Equal("pane-good", single.PaneId);
        Assert.Equal("conv-good", single.ConversationId);
    }

    [Fact]
    public async Task CompactAsync_FoldsDuplicatesAndDropsPanesNotInTheKnownSet()
    {
        var store = CreateStore();
        await store.RecordAsync(CreateRecord(paneId: "pane-keep", conversationId: "conv-1"));
        await store.RecordAsync(CreateRecord(paneId: "pane-keep", conversationId: "conv-2"));
        await store.RecordAsync(CreateRecord(paneId: "pane-gone", conversationId: "conv-x"));

        await store.CompactAsync(new HashSet<string>(StringComparer.Ordinal) { "pane-keep" });

        var loaded = await store.LoadAsync();
        var single = Assert.Single(loaded);
        Assert.Equal("pane-keep", single.PaneId);
        Assert.Equal("conv-2", single.ConversationId);

        // The file itself shrank to exactly one line — compaction rewrote it, not merely filtered on read.
        var lines = (await File.ReadAllLinesAsync(_path)).Where(line => !string.IsNullOrWhiteSpace(line)).ToList();
        Assert.Single(lines);
    }

    [Fact]
    public async Task LoadAsync_FileLockedExclusivelyByAnotherProcess_ReturnsEmptyRatherThanThrowing()
    {
        // The file exists and holds a valid record, but cannot be opened right now (a file lock, a permissions
        // problem, a genuinely corrupt handle) — the "unreadable file" case this ticket says must not stop the
        // cockpit from starting: an empty collection and a logged warning, never an exception to the caller.
        var store = CreateStore();
        await store.RecordAsync(CreateRecord());

        using var exclusiveLock = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None);

        var loaded = await store.LoadAsync();

        Assert.Empty(loaded);
    }

    [Fact]
    public async Task RecordAsync_UnwritablePath_DoesNotThrow()
    {
        var blockingFile = Path.Combine(Path.GetTempPath(), $"session-state-block-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(blockingFile, "not a directory");
        try
        {
            // A path *under* a regular file: the containing directory can never be created, so the append fails —
            // and must be swallowed to a warning rather than taking the caller (a live session) down.
            var store = new SessionStateStore(Path.Combine(blockingFile, "sub", "session-state.jsonl"), NullLogger<SessionStateStore>.Instance);

            Exception? caught = null;
            try
            {
                await store.RecordAsync(CreateRecord());
            }
            catch (Exception ex)
            {
                caught = ex;
            }

            Assert.Null(caught);
        }
        finally
        {
            File.Delete(blockingFile);
        }
    }

    [Fact]
    public async Task CompactAsync_WithoutARoster_FoldsDuplicatesAndDropsNothing()
    {
        // What the cockpit itself does at startup: it has no trustworthy list of panes that still exist, and the
        // wrong reading of that silence — "then none of them do" — would empty the file on the first start after
        // this store shipped. Folding without dropping is the only safe answer until panes are persisted.
        var store = CreateStore();
        await store.RecordAsync(CreateRecord(paneId: "pane-1", conversationId: "conv-old"));
        await store.RecordAsync(CreateRecord(paneId: "pane-1", conversationId: "conv-new"));
        await store.RecordAsync(CreateRecord(paneId: "pane-2", conversationId: "conv-other"));

        await store.CompactAsync();

        var loaded = await store.LoadAsync();
        Assert.Equal(2, loaded.Count);
        Assert.Equal("conv-new", Assert.Single(loaded, record => record.PaneId == "pane-1").ConversationId);

        // Read back on the file rather than through LoadAsync: the load folds duplicates anyway, so only the line
        // count shows that compaction actually rewrote three records down to two.
        var lines = (await File.ReadAllLinesAsync(_path)).Where(line => !string.IsNullOrWhiteSpace(line)).ToList();
        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public async Task CompactAsync_FileWhoseEveryLineIsUnreadable_LeavesItAloneRatherThanEmptyingIt()
    {
        // Found by running the real app against a hand-written state file (AC-410 live check): every line failed to
        // parse, the parse loop yielded nothing — it skips a bad line rather than raising — and compaction wrote
        // that nothing back out, truncating the file to zero bytes. A build that cannot read the file it is given
        // must not be the thing that destroys it; that is the same rule the read-failure path already follows.
        var store = CreateStore();
        await File.WriteAllTextAsync(_path, "{\"PaneId\":\"pane-1\",\"WorkingDirectory\":\"C:\\Users\\raymo\"}" + Environment.NewLine);
        var before = await File.ReadAllTextAsync(_path);

        await store.CompactAsync();

        Assert.Equal(before, await File.ReadAllTextAsync(_path));
    }

    [Fact]
    public async Task LoadAsync_LineWithoutAPaneId_SkipsThatLineAndKeepsTheRest()
    {
        // Valid JSON that simply has no pane on it — a hand edit of a file that sits next to cockpit.json, or a
        // truncation that happens to land on a closing brace. The serializer hands such a line back as a record
        // with a null pane rather than refusing it, and keying a dictionary on that null throws; unguarded, that
        // throw escapes the read and turns one bad line into "there is no session state at all", which is exactly
        // the loss that skipping a bad line exists to prevent.
        var store = CreateStore();
        await store.RecordAsync(CreateRecord(paneId: "pane-1", conversationId: "conv-1"));
        await File.AppendAllTextAsync(_path, "{\"ProfileId\":\"personal\"}" + Environment.NewLine);
        await store.RecordAsync(CreateRecord(paneId: "pane-2", conversationId: "conv-2"));

        var loaded = await store.LoadAsync();

        Assert.Equal(2, loaded.Count);
        Assert.Contains(loaded, record => record.PaneId == "pane-1" && record.ConversationId == "conv-1");
        Assert.Contains(loaded, record => record.PaneId == "pane-2" && record.ConversationId == "conv-2");
    }
}
