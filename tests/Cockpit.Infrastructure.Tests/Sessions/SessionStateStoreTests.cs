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

    /// <summary>
    /// Criteria 3 and 4 (AC-409): a session that ends without a graceful shutdown must still leave every write it
    /// made behind. The store that wrote is simply abandoned — RecordAsync is its only write path — and a brand-new
    /// instance reads the file back, so nothing here can pass on a record the writer merely cached. That read is
    /// also the "last record per pane wins" rule, for every field and across two panes at once.
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

    // Two lines a crash or a hand edit leaves: one cut off mid-append at the end of the file, and valid JSON with no pane
    // on it between two good ones. The serializer hands the latter back as a record with a null pane, and keying a
    // dictionary on that null would throw the whole read away — the loss that skipping a bad line exists to prevent.
    [Theory]
    [InlineData("{\"PaneId\":\"pane-bad\",\"ProfileId\":\"pers", false)]
    [InlineData("{\"ProfileId\":\"personal\"}\n", true)]
    public async Task LoadAsync_ABadLine_IsSkippedAndTheRecordsAroundItAreKept(string badLine, bool aRecordFollows)
    {
        var store = CreateStore();
        await store.RecordAsync(CreateRecord(paneId: "pane-1", conversationId: "conv-1"));
        await File.AppendAllTextAsync(_path, badLine);
        if (aRecordFollows)
        {
            await store.RecordAsync(CreateRecord(paneId: "pane-2", conversationId: "conv-2"));
        }

        var loaded = await store.LoadAsync();

        Assert.Equal(aRecordFollows ? ["pane-1", "pane-2"] : ["pane-1"], loaded.Select(record => record.PaneId).Order());
        Assert.Contains(loaded, record => record.PaneId == "pane-1" && record.ConversationId == "conv-1");
    }

    // With a roster, a pane not on it is dropped; without one — what the cockpit has at startup — nothing is, since
    // reading that silence as "then none of them exist" would empty the file. Either way duplicates fold, and the line
    // count shows compaction rewrote the file rather than merely filtering on read.
    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    public async Task CompactAsync_FoldsDuplicates_AndDropsOnlyPanesOffAGivenRoster(bool withRoster, int remaining)
    {
        var store = CreateStore();
        await store.RecordAsync(CreateRecord(paneId: "pane-keep", conversationId: "conv-1"));
        await store.RecordAsync(CreateRecord(paneId: "pane-keep", conversationId: "conv-2"));
        await store.RecordAsync(CreateRecord(paneId: "pane-gone", conversationId: "conv-x"));

        await store.CompactAsync(withRoster ? new HashSet<string>(StringComparer.Ordinal) { "pane-keep" } : null);

        var loaded = await store.LoadAsync();
        Assert.Equal(remaining, loaded.Count);
        Assert.Equal("conv-2", Assert.Single(loaded, record => record.PaneId == "pane-keep").ConversationId);
        Assert.Equal(remaining, (await File.ReadAllLinesAsync(_path)).Count(line => !string.IsNullOrWhiteSpace(line)));
    }
}
