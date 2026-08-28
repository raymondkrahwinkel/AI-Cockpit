using System.Diagnostics;
using Cockpit.Core.Assistant;
using Cockpit.Infrastructure.Assistant;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Core.Tests.Assistant;

// AC-1151: AssistantTranscriptFile.SaveAsync used to serialise and replace the whole file on every
// CollectionChanged event (AC-1142 measured 981 kB / four writes a minute on the live cockpit, growing with the
// session — O(n) per row, O(n^2) per sitting). SaveAsync now debounces: at most one actual write per window,
// plus one more via FlushAsync (ArchiveAsync, DisposeAsync) so neither ever sees a write still counting down.
public class Ac1151_TranscriptSaveDebounceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;

    public Ac1151_TranscriptSaveDebounceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "assistant-transcript.json");
    }

    private static AssistantTranscriptSnapshotEntry _Entry(string text) =>
        new("UserText", text, null, null, null, null, false, DateTimeOffset.Now);

    // THE ACCEPTANCE TEST (red before AC-1151, green after). Every SaveAsync used to write synchronously, so five
    // calls meant five writes; debounced, five calls inside the window coalesce into one, and the last one wins —
    // same "snapshot, not a trail" contract `ASecondSave_ReplacesTheFirst` already established.
    [Fact]
    public async Task ChangesWithinTheDebounceWindow_ProduceAtMostOneWrite()
    {
        var store = new AssistantTranscriptFile(_filePath, NullLogger<AssistantTranscriptFile>.Instance, TimeSpan.FromMilliseconds(200));

        var saves = Enumerable.Range(0, 5).Select(i => store.SaveAsync([_Entry($"row {i}")])).ToArray();
        await Task.WhenAll(saves);

        Assert.Equal(1, store.WriteCountForTests);
        Assert.Equal("row 4", Assert.Single(await store.LoadAsync()).Text);
    }

    // THE ACCEPTANCE TEST, portable version. Only the 2-arg constructor (all the pre-AC-1151 class had), so this
    // runs — red, 8 writes — against the code being replaced, and green, 1, against this change. Counts the
    // uniquely-GUID-named `.new` sidecar `ReplaceAtomicallyPrivate` creates per real write (CockpitConfigPath.cs).
    [Fact]
    public async Task ChangesWithinTheDebounceWindow_ProduceAtMostOnePhysicalWriteToDisk()
    {
        var store = new AssistantTranscriptFile(_filePath, NullLogger<AssistantTranscriptFile>.Instance);

        var writes = 0;
        using var watcher = new FileSystemWatcher(_tempDir, Path.GetFileName(_filePath) + ".*.new")
        {
            EnableRaisingEvents = true,
        };
        watcher.Created += (_, _) => Interlocked.Increment(ref writes);

        var saves = Enumerable.Range(0, 8).Select(i => store.SaveAsync([_Entry($"row {i}")])).ToArray();
        await Task.WhenAll(saves);
        await Task.Delay(TimeSpan.FromSeconds(1)); // lets the watcher's background thread drain pending events

        Assert.True(writes <= 1, $"expected at most one physical write, observed {writes}");
    }

    // Coalescing bounds the rate, it does not drop a later, separate change: once a window's write has landed, the
    // next call starts a fresh cycle of its own.
    [Fact]
    public async Task AChangeAfterTheWindowElapses_StartsANewWrite()
    {
        var store = new AssistantTranscriptFile(_filePath, NullLogger<AssistantTranscriptFile>.Instance, TimeSpan.FromMilliseconds(50));

        await store.SaveAsync([_Entry("first")]);
        await store.SaveAsync([_Entry("second")]);

        Assert.Equal(2, store.WriteCountForTests);
        Assert.Equal("second", Assert.Single(await store.LoadAsync()).Text);
    }

    // AC-1134/AC-1151: shutdown must still land the last rows on disk without itself eating the exit budget.
    // The window here (30s) is deliberately far longer than any budget — if this passed by the window elapsing
    // naturally, it would time out; it passes because DisposeAsync forces the flush instead of waiting it out.
    [Fact]
    public async Task DisposeAsync_FlushesAPendingWriteImmediately_RatherThanWaitingOutTheWindow()
    {
        var store = new AssistantTranscriptFile(_filePath, NullLogger<AssistantTranscriptFile>.Instance, TimeSpan.FromSeconds(30));
        _ = store.SaveAsync([_Entry("before the crash")]);

        var stopwatch = Stopwatch.StartNew();
        await store.DisposeAsync();
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"took {stopwatch.ElapsedMilliseconds}ms");
        Assert.Equal(1, store.WriteCountForTests);
        Assert.Equal("before the crash", Assert.Single(await store.LoadAsync()).Text);
    }

    // AC-1151: an archive must never lose rows still waiting out the debounce window — ArchiveAsync flushes them
    // first (same long, deliberately-not-naturally-elapsing window as above), so the moved file holds what was
    // actually saved, not whatever happened to have reached disk by the time the archive ran.
    [Fact]
    public async Task ArchiveAsync_FlushesAPendingWriteFirst_RatherThanArchivingStaleContent()
    {
        var store = new AssistantTranscriptFile(_filePath, NullLogger<AssistantTranscriptFile>.Instance, TimeSpan.FromSeconds(30));
        _ = store.SaveAsync([_Entry("still pending when archived")]);

        await store.ArchiveAsync();

        var archived = Assert.Single(Directory.GetFiles(_tempDir, "assistant-transcript.previous-*.json"));
        var loaded = await new AssistantTranscriptFile(archived, NullLogger<AssistantTranscriptFile>.Instance).LoadAsync();
        Assert.Equal("still pending when archived", Assert.Single(loaded).Text);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
