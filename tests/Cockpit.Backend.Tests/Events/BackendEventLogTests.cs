using System.Diagnostics;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Events;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.Backend.Tests.Events;

public sealed class BackendEventLogTests
{
    [Fact]
    public async Task ReconnectReplaysExactlyTheEventsAfterTheCursor_AndNoCursorStartsLive()
    {
        var log = new BackendEventLog();
        var first = log.Append("row", "pane-a", new { value = 1 });
        var second = log.Append("row", "pane-a", new { value = 2 });
        await using var replay = log.ReadFromAsync(first, CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await replay.MoveNextAsync());
        Assert.Equal(second, replay.Current.Seq);

        await using var live = log.ReadFromAsync(-1, CancellationToken.None).GetAsyncEnumerator();
        var waiting = live.MoveNextAsync().AsTask();
        var third = log.Append("row", null, new { value = 3 });
        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(third, live.Current.Seq);
        Assert.Equal("row", live.Current.Kind);
        Assert.Null(live.Current.PaneId);
    }

    [Fact]
    public async Task CursorOlderThanRingGetsReset_ButCursorInsideItDoesNot()
    {
        var log = new BackendEventLog();
        var first = log.Append("row", null, 0);
        Array.ForEach(Enumerable.Range(1, 10_000).ToArray(), value => log.Append("row", null, value));

        await using var stale = log.ReadFromAsync(first - 1, CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await stale.MoveNextAsync());
        Assert.Equal("reset", stale.Current.Kind);
        Assert.True(await stale.MoveNextAsync());
        Assert.Equal("row", stale.Current.Kind);

        await using var current = log.ReadFromAsync(first + 1, CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await current.MoveNextAsync());
        Assert.Equal("row", current.Current.Kind);
    }

    [Fact]
    public async Task SlowReaderGetsReset_WithoutSlowingAppend()
    {
        var log = new BackendEventLog();
        await using var reader = log.ReadFromAsync(-1, CancellationToken.None).GetAsyncEnumerator();
        var waiting = reader.MoveNextAsync().AsTask();
        log.Append("row", null, 0);
        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(1)));

        var clock = Stopwatch.StartNew();
        Array.ForEach(Enumerable.Range(1, 1_000).ToArray(), value => log.Append("row", null, value));
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"Append took {clock.Elapsed}.");
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal("reset", reader.Current.Kind);
    }

    [Fact]
    public void BackendEventsAndTranscriptUpsertsShareOneSequence()
    {
        var log = new BackendEventLog();
        var host = new SessionHost<QueuedPrompt>(() => "pane-a", null, TimeProvider.System);
        TranscriptRowUpsert? upsert = null;
        host.RowUpserted += row => upsert = row;
        var before = log.Append("first", null, new { });
        host.RecordRow(new TranscriptSnapshotEntry("row", "UserText", "hello", null, null, null, null, false, DateTimeOffset.UtcNow));
        var after = log.Append("last", null, new { });

        Assert.NotNull(upsert);
        Assert.True(before < upsert.Value.Seq && upsert.Value.Seq < after);
    }
}
