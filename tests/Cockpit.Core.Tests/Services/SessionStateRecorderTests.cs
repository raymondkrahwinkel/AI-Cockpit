using Cockpit.App.Services;
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
    /// Raymond's follow-up decision on AC-513: a saved id is only safe to keep once "start fresh" resumes the
    /// exact place it was saved in — a different working directory means the provider on the other end of that id
    /// has no reason to recognise this pane's new context, so <c>RecordSessionStartedAsync</c> must clear it back
    /// to <see cref="SessionConversationIdState.Unknown"/> rather than let it ride until a new report happens to
    /// replace it (which might never come, e.g. an <c>Unsupported</c> provider).
    /// </summary>
    [Fact]
    public async Task RecordSessionStartedAsync_ChangedWorkingDirectory_ClearsTheSavedConversationId()
    {
        var store = CreateStore();
        await store.RecordAsync(CreateRecord()); // WorkingDirectory: "/repo"

        var recorder = CreateRecorder(store);
        await recorder.RecordSessionStartedAsync("pane-1", WorkProfile, "/repo-two", null, null, "default");

        var record = Assert.Single(await store.LoadAsync());
        Assert.Null(record.ConversationId);
        Assert.Equal(SessionConversationIdState.Unknown, record.ConversationState);
    }

    /// <summary>The other half of the same decision: an unchanged directory must not lose the saved id, isolated from the profile check below by keeping the profile fixed.</summary>
    [Fact]
    public async Task RecordSessionStartedAsync_UnchangedWorkingDirectory_KeepsTheSavedConversationId()
    {
        var store = CreateStore();
        await store.RecordAsync(CreateRecord());

        var recorder = CreateRecorder(store);
        await recorder.RecordSessionStartedAsync("pane-1", WorkProfile, "/repo", null, null, "default");

        var record = Assert.Single(await store.LoadAsync());
        Assert.Equal("conv-old", record.ConversationId);
        Assert.Equal(SessionConversationIdState.Known, record.ConversationState);
    }

    [Fact]
    public async Task RecordSessionStartedAsync_ForgetConversation_ClearsTheSavedConversationIdInTheSameContext()
    {
        var store = CreateStore();
        await store.RecordAsync(CreateRecord());

        var recorder = CreateRecorder(store);
        await recorder.RecordSessionStartedAsync(
            "pane-1", WorkProfile, "/repo", null, null, "default", forgetConversation: true);

        var record = Assert.Single(await store.LoadAsync());
        Assert.Null(record.ConversationId);
        Assert.Equal(SessionConversationIdState.Unknown, record.ConversationState);
    }

    /// <summary>
    /// The naive comparison this guard deliberately avoids: a bare string check would treat "/repo" and "/repo/"
    /// as different places and wipe an id that never actually moved. <see cref="Cockpit.Core.WorkingPaths.DirectoryPath"/>
    /// is the same folder-equality rule the worktree engine itself uses, and this proves the guard actually goes
    /// through it rather than an ordinal compare.
    /// </summary>
    [Fact]
    public async Task RecordSessionStartedAsync_SameDirectoryWithATrailingSeparator_KeepsTheSavedConversationId()
    {
        var store = CreateStore();
        await store.RecordAsync(CreateRecord()); // WorkingDirectory: "/repo"

        var recorder = CreateRecorder(store);
        await recorder.RecordSessionStartedAsync("pane-1", WorkProfile, "/repo/", null, null, "default");

        var record = Assert.Single(await store.LoadAsync());
        Assert.Equal("conv-old", record.ConversationId);
    }

    /// <summary>The profile side of the same decision, isolated from the directory check above by keeping the directory fixed.</summary>
    [Fact]
    public async Task RecordSessionStartedAsync_ChangedProfile_ClearsTheSavedConversationId()
    {
        var store = CreateStore();
        await store.RecordAsync(CreateRecord()); // ProfileId: "work"

        var recorder = CreateRecorder(store);
        var personalProfile = new SessionProfile("personal", new ClaudeConfig(@"C:\fake\.claude"));
        await recorder.RecordSessionStartedAsync("pane-1", personalProfile, "/repo", null, null, "default");

        var record = Assert.Single(await store.LoadAsync());
        Assert.Null(record.ConversationId);
        Assert.Equal(SessionConversationIdState.Unknown, record.ConversationState);
    }

    /// <summary>
    /// A pane with no saved state at all has nothing this guard could lose: its synthesized blank record already
    /// carries a null id and <c>Unknown</c> state, so comparing it against the real profile/directory and finding
    /// them "different" is harmless. Proves a brand-new pane's very first <c>RecordSessionStartedAsync</c> call
    /// still lands its own fields normally rather than tripping on the new comparison.
    /// </summary>
    [Fact]
    public async Task RecordSessionStartedAsync_ForABrandNewPaneWithNoSavedState_LandsNormally()
    {
        var store = CreateStore();
        var recorder = CreateRecorder(store);

        await recorder.RecordSessionStartedAsync("pane-1", WorkProfile, "/repo", null, null, "acceptEdits");

        var record = Assert.Single(await store.LoadAsync());
        Assert.Null(record.ConversationId);
        Assert.Equal(SessionConversationIdState.Unknown, record.ConversationState);
        Assert.Equal("work", record.ProfileId);
        Assert.Equal("/repo", record.WorkingDirectory);
        Assert.Equal("acceptEdits", record.PermissionMode);
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
    /// Scopes the guard above: Unsupported is the provider stating a fact about itself ("no resumable conversation
    /// at all"), not "not known yet" — unlike Unknown, it is allowed to replace an already-Known id. Proves the fix
    /// for criterion 3 did not overreach into refusing every non-Known report.
    /// </summary>
    [Fact]
    public async Task AnUnsupportedConversationReport_DoesOverwriteAnAlreadyKnownConversationId()
    {
        var store = CreateStore();
        var tracker = new SessionConversationTracker();
        var recorder = CreateRecorder(store, tracker);

        tracker.Report("pane-1", SessionConversationId.Known("conv-1"));
        Assert.NotNull(await _WaitForAsync(store, "pane-1", record => record.ConversationId == "conv-1"));

        tracker.Report("pane-1", SessionConversationId.Unsupported);

        var updated = await _WaitForAsync(store, "pane-1", record => record.ConversationState == SessionConversationIdState.Unsupported);
        Assert.NotNull(updated);
        Assert.Null(updated!.ConversationId);
    }

    /// <summary>
    /// The guard's scoping the other way round (mutation M7 in the review: dropping the "only when Known" clause
    /// entirely, so Unknown never overwrites anything). The existing "does not overwrite an already-Known id" test
    /// above cannot tell this apart from the real guard, because in that test <c>existing</c> already is Known —
    /// both the real guard and the over-broad mutant agree there. This one starts from a non-Known state, where
    /// they disagree: the real guard lets Unknown write through (it is only Known it refuses to clobber), so the
    /// state actually moves from Unsupported to Unknown; the mutant would leave it stuck on Unsupported.
    /// </summary>
    [Fact]
    public async Task AnUnknownConversationReport_DoesOverwriteANotKnownConversationState()
    {
        var store = CreateStore();
        var tracker = new SessionConversationTracker();
        var recorder = CreateRecorder(store, tracker);

        tracker.Report("pane-1", SessionConversationId.Unsupported);
        Assert.NotNull(await _WaitForAsync(store, "pane-1", record => record.ConversationState == SessionConversationIdState.Unsupported));

        tracker.Report("pane-1", SessionConversationId.Unknown);

        var updated = await _WaitForAsync(store, "pane-1", record => record.ConversationState == SessionConversationIdState.Unknown);
        Assert.NotNull(updated);
    }

    /// <summary>
    /// The ticket's headline scenario end to end: a restart, an operator choosing "start fresh" on a restored pane,
    /// and only the new conversation's own reported id allowed to replace the old one. Combines the seed step
    /// <c>CockpitViewModel.RestoreSessionPanesAsync</c> actually takes with the write-path guard, since production
    /// takes both.
    /// </summary>
    [Fact]
    public async Task StartFresh_KeepsTheOldConversationIdUntilTheNewSessionReportsItsOwn()
    {
        var store = CreateStore();
        await store.RecordAsync(CreateRecord());

        var tracker = new SessionConversationTracker();
        var recorder = CreateRecorder(store, tracker);
        recorder.Seed(await store.LoadAsync());

        // "Start fresh": a new session starts on the same pane id, then its driver's very first event reports
        // Unknown (no session id from the provider yet).
        await recorder.RecordSessionStartedAsync("pane-1", WorkProfile, "/repo", null, null, "default");
        tracker.Report("pane-1", SessionConversationId.Unknown);
        await Task.Delay(200);

        var stillOld = Assert.Single(await store.LoadAsync());
        Assert.Equal("conv-old", stillOld.ConversationId);

        // The new session's provider now reports its own id — this, and only this, is allowed to replace it.
        tracker.Report("pane-1", SessionConversationId.Known("conv-new"));
        var replaced = await _WaitForAsync(store, "pane-1", record => record.ConversationId == "conv-new");

        Assert.NotNull(replaced);
    }

    /// <summary>
    /// <see cref="SessionStateRecorder.Seed"/> is the optimization <c>RestoreSessionPanesAsync</c> takes to avoid
    /// a second file read; this proves it actually primes the cache a write can then build on, independent of the
    /// write path's own lazy fallback (which the criterion-2 test above exercises instead).
    /// </summary>
    [Fact]
    public async Task Seed_PrimesTheCacheSoASubsequentWriteBuildsOnTheSavedRecord()
    {
        var store = CreateStore();
        await store.RecordAsync(CreateRecord());

        var recorder = CreateRecorder(store);
        recorder.Seed(await store.LoadAsync());

        await recorder.RecordPermissionModeChangedAsync("pane-1", "bypassPermissions");

        var record = Assert.Single(await store.LoadAsync());
        Assert.Equal("conv-old", record.ConversationId);
        Assert.Equal("bypassPermissions", record.PermissionMode);
    }

    /// <summary>
    /// Isolates <c>Seed</c> from the write path's own self-heal (mutation M1 in the review: <c>Seed</c> turned
    /// into a full no-op still passes every other test here, because the self-heal silently covers for it). The
    /// disk file deliberately holds a *different* id than the one handed to <c>Seed</c> — a real <c>Seed</c> call
    /// must win over self-heal without ever touching the store again, so only <c>Seed</c>'s value can appear in
    /// the resulting write; a no-op <c>Seed</c> would instead trigger the lazy load and surface the disk value.
    /// </summary>
    [Fact]
    public async Task Seed_TakesPrecedenceOverWhateverTheStoresFileActuallyHolds()
    {
        var store = CreateStore();
        await store.RecordAsync(CreateRecord(conversationId: "conv-on-disk"));

        var recorder = CreateRecorder(store);
        recorder.Seed([CreateRecord(conversationId: "conv-seeded")]);

        await recorder.RecordPermissionModeChangedAsync("pane-1", "bypassPermissions");

        var record = Assert.Single(await store.LoadAsync());
        Assert.Equal("conv-seeded", record.ConversationId);
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
    /// The write path's single-flight cache (mutation M6 in the review: dropping the <c>??=</c> assignment so
    /// <c>_seedTask</c> is never actually cached, and every write re-reads the store). A single reload would be
    /// invisible from the outside if the store never changed between writes — so this test changes the file
    /// directly, behind the recorder's back, between two writes: a correctly cached seed never sees that change;
    /// an uncached one reloads it and lets it silently overwrite the in-memory conversation id.
    /// </summary>
    [Fact]
    public async Task EnsureSeeded_LoadsTheStoreAtMostOnce_NotOnEveryWrite()
    {
        var store = CreateStore();
        await store.RecordAsync(CreateRecord(conversationId: "conv-initial"));

        var recorder = CreateRecorder(store);

        // Triggers the self-heal load; a correct implementation caches it from here on.
        await recorder.RecordPermissionModeChangedAsync("pane-1", "acceptEdits");

        // Something else appends to the file behind the recorder's back — a compaction, another process. A cached
        // seed must not go back to disk mid-run and pick this up.
        await store.RecordAsync(CreateRecord(conversationId: "conv-injected-behind-the-back"));

        // Never mentions ConversationId — if the cache is still the one from the first load, this builds on
        // "conv-initial", not on whatever the file holds by now.
        await recorder.RecordPermissionModeChangedAsync("pane-1", "bypassPermissions");

        var record = Assert.Single(await store.LoadAsync());
        Assert.Equal("conv-initial", record.ConversationId);
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
    /// Bevinding 2/3: a seed attempt that cannot read the store must not be read as "this pane has no saved
    /// state" — that would compose the write against a blank record and bury the real id the same way the
    /// ordering bug does. The write must be skipped instead, and a later write must retry the read rather than
    /// staying unseeded for the rest of the process.
    /// </summary>
    [Fact]
    public async Task AWriteThatCannotSeedTheCache_IsSkippedAndALaterWriteRetriesTheRead()
    {
        var realStore = CreateStore();
        await realStore.RecordAsync(CreateRecord(permissionMode: "resumed"));

        var loadAttempts = 0;
        var store = new InstrumentedSessionStateStore(realStore)
        {
            TryLoadOverride = async cancellationToken =>
            {
                loadAttempts++;
                return loadAttempts == 1 ? null : await realStore.TryLoadAsync(cancellationToken);
            },
        };

        var recorder = CreateRecorder(store);

        // The first write's seed attempt fails (simulated unreadable file). Composing against a blank cache here
        // would bury "conv-old" under a fresh record carrying only this write's permission mode.
        await recorder.RecordPermissionModeChangedAsync("pane-1", "acceptEdits");

        var afterFailedSeed = Assert.Single(await realStore.LoadAsync());
        Assert.Equal("conv-old", afterFailedSeed.ConversationId);
        Assert.Equal("resumed", afterFailedSeed.PermissionMode);

        // A later write must retry rather than being stuck unseeded — this one succeeds (loadAttempts reaches 2),
        // so it must build on the saved id.
        await recorder.RecordPermissionModeChangedAsync("pane-1", "bypassPermissions");

        var afterRetry = Assert.Single(await realStore.LoadAsync());
        Assert.Equal("conv-old", afterRetry.ConversationId);
        Assert.Equal("bypassPermissions", afterRetry.PermissionMode);
        Assert.Equal(2, loadAttempts);
    }

    // The sibling of the test above, for the way a read can fail that the store's contract does not describe.
    // TryLoadAsync answers null when it cannot tell — but it reads a file, and a file can go away between the
    // File.Exists check and the read. A throw there must land in the same place a null does: the seed task is
    // shared by every write queued behind the write gate and is awaited on a fire-and-forget path, so a faulted
    // task would be cached, rethrown by every later write into `_ = recorder.RecordXAsync(...)`, and silently end
    // all session-state recording for the rest of the process.
    [Fact]
    public async Task AStoreThatThrowsWhereItsContractSaysNull_DoesNotSilentlyEndAllRecording()
    {
        var realStore = CreateStore();
        await realStore.RecordAsync(CreateRecord(permissionMode: "resumed"));

        var loadAttempts = 0;
        var store = new InstrumentedSessionStateStore(realStore)
        {
            TryLoadOverride = async cancellationToken =>
            {
                loadAttempts++;
                return loadAttempts == 1
                    ? throw new IOException("the state file went away between the existence check and the read")
                    : await realStore.TryLoadAsync(cancellationToken);
            },
        };

        var recorder = CreateRecorder(store);

        // Skipped, not thrown out of: both production call sites discard the returned task, so an exception here
        // would surface nowhere at all.
        await recorder.RecordPermissionModeChangedAsync("pane-1", "acceptEdits");

        var afterThrow = Assert.Single(await realStore.LoadAsync());
        Assert.Equal("conv-old", afterThrow.ConversationId);
        Assert.Equal("resumed", afterThrow.PermissionMode);

        // And the failure must not have been cached: this write reads successfully and builds on the saved id.
        await recorder.RecordPermissionModeChangedAsync("pane-1", "bypassPermissions");

        var afterRetryFromThrow = Assert.Single(await realStore.LoadAsync());
        Assert.Equal("conv-old", afterRetryFromThrow.ConversationId);
        Assert.Equal("bypassPermissions", afterRetryFromThrow.PermissionMode);
        Assert.Equal(2, loadAttempts);
    }
}
