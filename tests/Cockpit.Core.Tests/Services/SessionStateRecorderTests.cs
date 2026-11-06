using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Core.Tests.Services;

/// <summary>
/// AC-513: <see cref="SessionStateRecorder"/> starts every process with an empty in-memory cache, so the first
/// write for a pane this run must not be composed against a blank record when the store's file already has a real
/// one — and a report that only says "not known yet" must not be read as "the known value is gone". Uses the real
/// <see cref="SessionStateStore"/> against a temp file rather than a fake: the bug this ticket fixes only shows up
/// through the store's own last-record-wins read, which a substitute would not reproduce. A handful of tests below
/// wrap that real store in <see cref="InstrumentedSessionStateStore"/> instead of replacing it, to force a specific
/// timing or a read failure while keeping the same real semantics.
/// </summary>
public sealed class SessionStateRecorderTests : IDisposable
{
    private static readonly SessionProfile WorkProfile = new("work", new ClaudeConfig(@"C:\fake\.claude"));

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"session-state-recorder-{Guid.NewGuid():N}.jsonl");

    private SessionStateStore CreateStore() => new(_path, NullLogger<SessionStateStore>.Instance);

    private static SessionStateRecorder CreateRecorder(ISessionStateStore store, SessionConversationTracker? tracker = null) =>
        new(store, tracker ?? new SessionConversationTracker(), NullLogger<SessionStateRecorder>.Instance);

    private static SessionStateRecord CreateRecord(
        string paneId = "pane-1",
        string? conversationId = "conv-old",
        SessionConversationIdState conversationState = SessionConversationIdState.Known,
        string? permissionMode = "default") =>
        new(paneId, "work", "ClaudeCli", conversationId, conversationState, "/repo", null, null, permissionMode, DateTimeOffset.UtcNow);

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    // _OnConversationChanged writes fire-and-forget (by design — a session must not stall on a state-file append),
    // so a test that reports a conversation change has to wait for that write to actually land rather than assert
    // immediately after Report returns.
    private static async Task<SessionStateRecord?> _WaitForAsync(SessionStateStore store, string paneId, Func<SessionStateRecord, bool> predicate, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(2));
        while (DateTime.UtcNow < deadline)
        {
            var loaded = await store.LoadAsync();
            if (loaded.FirstOrDefault(record => record.PaneId == paneId) is { } found && predicate(found))
            {
                return found;
            }

            await Task.Delay(10);
        }

        return null;
    }

    /// <summary>
    /// Criterion 2: a prior run wrote a real conversation id for this pane; the recorder that wrote it is gone
    /// (a fresh process, an in-memory cache starting empty), and the next thing this pane does is start a new
    /// session (<see cref="SessionStateRecorder.RecordSessionStartedAsync"/>) — resuming the exact same profile
    /// and directory the id was saved against, so the same-context guard added for the profile/directory-change
    /// fix leaves it alone. That write must not be composed against a blank record: this is the write path's own
    /// self-heal (<c>_EnsureSeededAsync</c>), not <c>Seed</c> — no seed call is made here on purpose, to prove the
    /// guarantee does not depend on <c>CockpitViewModel</c> calling it first.
    /// </summary>
    [Fact]
    public async Task RecordSessionStartedAsync_AfterARestart_DoesNotEraseTheSavedConversationId()
    {
        var store = CreateStore();
        await store.RecordAsync(CreateRecord());

        // A fresh recorder, as a restarted process would construct: no Seed call, nothing carried over in memory.
        var recorder = CreateRecorder(store);

        // Same profile ("work") and same directory ("/repo") as the seeded record — resuming in place.
        await recorder.RecordSessionStartedAsync("pane-1", WorkProfile, "/repo", null, null, "acceptEdits");

        var loaded = await store.LoadAsync();
        var record = Assert.Single(loaded);
        Assert.Equal("conv-old", record.ConversationId);
        Assert.Equal(SessionConversationIdState.Known, record.ConversationState);
        // The write did happen — its own fields landed — this is not merely a no-op.
        Assert.Equal("acceptEdits", record.PermissionMode);
    }

    /// <summary>
    /// AC-513 follow-up: a saved id is only safe to keep when "start fresh" resumes the exact place it was saved in —
    /// a different working directory clears it back to <see cref="SessionConversationIdState.Unknown"/>. AC-1261: a
    /// clear changes neither profile nor directory, so without <c>forgetConversation</c> a cockpit closed before any
    /// new id arrives would resume the very conversation the operator just cleared.
    /// </summary>
    [Theory]
    [InlineData("/repo-two", false)]
    [InlineData("/repo", true)]
    public async Task RecordSessionStartedAsync_ChangedWorkingDirectoryOrForgetConversation_ClearsTheSavedConversationId(string directory, bool forget)
    {
        var store = CreateStore();
        await store.RecordAsync(CreateRecord()); // profile "work", WorkingDirectory "/repo"

        var recorder = CreateRecorder(store);
        await recorder.RecordSessionStartedAsync("pane-1", WorkProfile, directory, null, null, "default", forgetConversation: forget);

        var record = Assert.Single(await store.LoadAsync());
        Assert.Null(record.ConversationId);
        Assert.Equal(SessionConversationIdState.Unknown, record.ConversationState);
    }

    /// <summary>
    /// Criterion 3, isolated from criterion 2: no restart, no cache miss — a single recorder, mid-run, receives an
    /// Unknown conversation report for a pane whose conversation id is already Known. <see cref="IPluginSessionDriver.Conversation"/>'s
    /// own default is Unknown before a provider's session id is set, so this is not a hypothetical: every fresh
    /// session start (crucially, "start fresh" after a restart) reports it before it ever reports anything real.
    /// Raymond's design call: the saved id stays until the new conversation reports one of its own.
    /// </summary>
    [Fact]
    public async Task AnUnknownConversationReport_DoesNotOverwriteAnAlreadyKnownConversationId()
    {
        var store = CreateStore();
        var tracker = new SessionConversationTracker();
        var recorder = CreateRecorder(store, tracker);

        tracker.Report("pane-1", SessionConversationId.Known("conv-1"));
        Assert.NotNull(await _WaitForAsync(store, "pane-1", record => record.ConversationId == "conv-1"));

        tracker.Report("pane-1", SessionConversationId.Unknown);

        // Give the Unknown write the same window as a real change would get, then assert it did not win.
        await Task.Delay(200);
        var loaded = await store.LoadAsync();
        var record = Assert.Single(loaded);
        Assert.Equal("conv-1", record.ConversationId);
        Assert.Equal(SessionConversationIdState.Known, record.ConversationState);
    }

    /// <summary>
    /// The other direction of the guard in <c>Seed</c> (mutation M2 in the review: dropping the
    /// "<c>if (_seedTask is not null) return;</c>" early-out lets a later <c>Seed</c> call clobber whatever
    /// already seeded the cache). Here the write path's own self-heal wins the race first — the class's documented
    /// scenario — and a stale snapshot then reaches <c>Seed</c>; the real guard must leave the fresher, already-
    /// self-healed value alone.
    /// </summary>
    [Fact]
    public async Task Seed_DoesNotOverwriteAFresherLoadTheWritePathAlreadyPerformed()
    {
        var store = CreateStore();
        await store.RecordAsync(CreateRecord(conversationId: "conv-on-disk"));

        var recorder = CreateRecorder(store);

        // No Seed() call yet — this write triggers the self-heal load from disk, per the class's own documented
        // race between RestoreSessionPanesAsync's Seed call and a write that reaches the recorder first.
        await recorder.RecordPermissionModeChangedAsync("pane-1", "acceptEdits");

        // Seed lands after the self-heal already won, handing in a stale snapshot it read earlier.
        recorder.Seed([CreateRecord(conversationId: "conv-stale-seed")]);

        await recorder.RecordPermissionModeChangedAsync("pane-1", "bypassPermissions");

        var record = Assert.Single(await store.LoadAsync());
        Assert.Equal("conv-on-disk", record.ConversationId);
    }

    /// <summary>
    /// Bevinding 1: reproduces AC-513's own measured ordering bug deterministically, without relying on a
    /// thread-pool race. <c>Seed</c> resolves the cache synchronously so both reports below run their compose step
    /// inline, in the exact order <c>Report</c> is called — isolating the assertion to whether the two
    /// <c>RecordAsync</c> calls that follow land on disk in call order, which is what AC-513's write-gate exists to
    /// guarantee. The write that should stay old (the Unknown report, kept by criterion 3's guard) is deliberately
    /// the one held back from actually reaching the store — exactly the shape the review measured: the write that
    /// composed first is not guaranteed to be the one whose append lands first.
    /// </summary>
    [Fact]
    public async Task ConcurrentWrites_ForTheSamePane_PersistInCallOrder_EvenWhenTheEarlierWritesAppendIsSlow()
    {
        var realStore = CreateStore();
        var oldWriteGate = new TaskCompletionSource();
        var store = new InstrumentedSessionStateStore(realStore)
        {
            BeforeRecord = (record, _) => record.ConversationId == "conv-old" ? oldWriteGate.Task : Task.CompletedTask,
        };

        var tracker = new SessionConversationTracker();
        var recorder = CreateRecorder(store, tracker);
        recorder.Seed([CreateRecord()]);

        // AC-513's restart race: a driver reports Unknown, then its real (new) conversation id, in immediate
        // succession. The Unknown write's own append is now blocked on oldWriteGate above.
        tracker.Report("pane-1", SessionConversationId.Unknown);
        tracker.Report("pane-1", SessionConversationId.Known("conv-new"));

        // Give the newer write a window to land on its own — without the write-gate it is not blocked on
        // anything and can complete immediately; with the write-gate it cannot even start yet, so this simply
        // finds nothing and moves on.
        await _WaitForAsync(realStore, "pane-1", record => record.ConversationId == "conv-new", TimeSpan.FromMilliseconds(300));

        oldWriteGate.SetResult();

        var final = await _WaitForAsync(realStore, "pane-1", record => record.ConversationId == "conv-new");
        Assert.NotNull(final);
        Assert.Equal("conv-new", final!.ConversationId);
    }

    /// <summary>
    /// Bevinding 2/3: a seed attempt that cannot read the store — a null, or a throw where the contract says null (a
    /// file gone between the existence check and the read) — must not compose the write against a blank record and
    /// bury the real id. It is skipped, the failure is not cached (the seed task is shared and awaited on a
    /// fire-and-forget path, so a cached fault would silently end all recording), and a later write retries the read.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AWriteThatCannotSeedTheCache_IsSkippedAndALaterWriteRetriesTheRead(bool readThrows)
    {
        var realStore = CreateStore();
        await realStore.RecordAsync(CreateRecord(permissionMode: "resumed"));

        var loadAttempts = 0;
        var store = new InstrumentedSessionStateStore(realStore)
        {
            TryLoadOverride = async cancellationToken =>
            {
                loadAttempts++;
                if (loadAttempts > 1)
                {
                    return await realStore.TryLoadAsync(cancellationToken);
                }

                if (readThrows)
                {
                    throw new IOException("the state file went away between the check and the read");
                }

                return null;
            },
        };

        var recorder = CreateRecorder(store);

        await recorder.RecordPermissionModeChangedAsync("pane-1", "acceptEdits");

        var afterFailedSeed = Assert.Single(await realStore.LoadAsync());
        Assert.Equal("conv-old", afterFailedSeed.ConversationId);
        Assert.Equal("resumed", afterFailedSeed.PermissionMode);

        await recorder.RecordPermissionModeChangedAsync("pane-1", "bypassPermissions");

        var afterRetry = Assert.Single(await realStore.LoadAsync());
        Assert.Equal("conv-old", afterRetry.ConversationId);
        Assert.Equal("bypassPermissions", afterRetry.PermissionMode);
        Assert.Equal(2, loadAttempts);
    }
}
