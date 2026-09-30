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
