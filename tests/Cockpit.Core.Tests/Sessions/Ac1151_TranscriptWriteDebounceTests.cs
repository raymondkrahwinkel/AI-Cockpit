using System.Diagnostics;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Core.Tests.Sessions;

// AC-1151 debounced the assistant transcript's writes; AC-1090 kept the debounce and changed what it coalesces —
// which rows changed in a window, rather than how often a whole file is rewritten. Both are held here: the window
// still produces at most one write, and the log's cost scales with the rows that changed, not the transcript.
public class Ac1151_TranscriptWriteDebounceTests : IDisposable
{
    private const string Pane = "pane-1";

    private readonly string _tempDir;

    public Ac1151_TranscriptWriteDebounceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    private static TranscriptSnapshotEntry _Entry(string id, string text) =>
        new(id, "UserText", text, null, null, null, null, false, DateTimeOffset.Now);

    // Five rows arriving inside one window cost one write, not five — the burst a single turn produces.
    [Fact]
    public async Task RowsWithinTheDebounceWindow_ProduceAtMostOneWrite()
    {
        var store = new SessionTranscriptLog(_tempDir, NullLogger<SessionTranscriptLog>.Instance, TimeSpan.FromMilliseconds(200));

        var writes = Enumerable.Range(0, 5).Select(i => store.AppendAsync(Pane, _Entry($"row-{i}", $"row {i}"))).ToArray();
        await Task.WhenAll(writes);

        Assert.Equal(1, store.WriteCountForTests);
        Assert.Equal(5, (await store.TryLoadAsync(Pane))!.Count);
    }

    // The same row changing repeatedly inside one window — a streaming reply — costs one line, not one per delta.
    // Without the coalescing this is the shape that would put the amplification straight back.
    [Fact]
    public async Task OneRowChangingRepeatedlyWithinTheWindow_IsWrittenOnce_InItsLastVersion()
    {
        var store = new SessionTranscriptLog(_tempDir, NullLogger<SessionTranscriptLog>.Instance, TimeSpan.FromMilliseconds(200));

        var writes = Enumerable.Range(0, 20).Select(i => store.AppendAsync(Pane, _Entry("row-a", new string('x', i + 1)))).ToArray();
        await Task.WhenAll(writes);

        Assert.Equal(1, store.WriteCountForTests);
        var loaded = Assert.Single((await store.TryLoadAsync(Pane))!);
        Assert.Equal(new string('x', 20), loaded.Text);
    }

    // THE ACCEPTANCE TEST for AC-1090 criterion 1: recording a long conversation costs what its rows cost, not
    // the transcript over and over. Red at 2500x against a store that rewrites the whole log per flush, green
    // here. The debounce is off, so no coalescing hides anything.
    [Fact]
    public async Task RecordingALongConversation_CostsAboutWhatItStores_RatherThanTheTranscriptOverAndOver()
    {
        var store = new SessionTranscriptLog(_tempDir, NullLogger<SessionTranscriptLog>.Instance, TimeSpan.Zero);

        // A thousand rows of about a kilobyte each, every one recorded five times over — a row streams in, its
        // result lands, a permission is decided. Five versions is the cost this log is allowed to have.
        for (var row = 0; row < 1000; row++)
        {
            for (var version = 0; version < 5; version++)
            {
                await store.AppendAsync(Pane, _Entry($"row-{row}", new string('x', 1000)));
            }
        }

        var finalSize = new FileInfo(store.LogPath(Pane)).Length;
        var amplification = (double)store.BytesWrittenForTests / finalSize;

        Assert.True(amplification < 1.05, $"expected the log to be written about once, was {amplification:F1}x");

        // And the log itself is bounded by what it was told, not by the transcript squared: five versions of a
        // thousand rows is five thousand lines, nothing more.
        Assert.True(finalSize < 6_000_000, $"log grew to {finalSize} bytes for 5,000 recorded row versions");
        Assert.Equal(1000, (await store.TryLoadAsync(Pane))!.Count);
    }

    // Coalescing bounds the rate, it does not drop a later, separate change: once a window's write has landed, the
    // next call starts a fresh cycle of its own.
    [Fact]
    public async Task AChangeAfterTheWindowElapses_StartsANewWrite()
    {
        var store = new SessionTranscriptLog(_tempDir, NullLogger<SessionTranscriptLog>.Instance, TimeSpan.FromMilliseconds(50));

        await store.AppendAsync(Pane, _Entry("row-a", "first"));
        await store.AppendAsync(Pane, _Entry("row-b", "second"));

        Assert.Equal(2, store.WriteCountForTests);
        Assert.Equal(["first", "second"], (await store.TryLoadAsync(Pane))!.Select(entry => entry.Text));
    }

    // AC-1134/AC-1151: shutdown must still land the last rows on disk without itself eating the exit budget.
    // The window here (30s) is deliberately far longer than any budget — if this passed by the window elapsing
    // naturally, it would time out; it passes because DisposeAsync forces the flush instead of waiting it out.
    [Fact]
    public async Task DisposeAsync_FlushesAPendingWriteImmediately_RatherThanWaitingOutTheWindow()
    {
        var store = new SessionTranscriptLog(_tempDir, NullLogger<SessionTranscriptLog>.Instance, TimeSpan.FromSeconds(30));
        _ = store.AppendAsync(Pane, _Entry("row-a", "before the crash"));

        var stopwatch = Stopwatch.StartNew();
        await store.DisposeAsync();
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"took {stopwatch.ElapsedMilliseconds}ms");
        Assert.Equal(1, store.WriteCountForTests);
        Assert.Equal("before the crash", Assert.Single((await store.TryLoadAsync(Pane))!).Text);
    }

    // AC-1151: an archive must never lose rows still waiting out the debounce window — ArchiveAsync flushes them
    // first (same long, deliberately-not-naturally-elapsing window as above), so the rolled-aside log holds what
    // was actually recorded, not whatever happened to have reached disk by the time the archive ran.
    [Fact]
    public async Task ArchiveAsync_FlushesAPendingWriteFirst_RatherThanArchivingStaleContent()
    {
        var store = new SessionTranscriptLog(_tempDir, NullLogger<SessionTranscriptLog>.Instance, TimeSpan.FromSeconds(30));
        _ = store.AppendAsync(Pane, _Entry("row-a", "still pending when archived"));

        await store.ArchiveAsync(Pane);

        var archived = Assert.Single(Directory.GetFiles(_tempDir, $"{Pane}.previous-*.jsonl"));
        Assert.Contains("still pending when archived", await File.ReadAllTextAsync(archived), StringComparison.Ordinal);
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
