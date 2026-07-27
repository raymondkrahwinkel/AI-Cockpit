using Cockpit.Core.Usage;
using Cockpit.Infrastructure.Usage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Core.Tests.Usage;

/// <summary>
/// The usage trail (AC-251) as a file: a snapshot survives the process that wrote it, several sessions stay
/// apart, a run's sessions can be found back by their run id, and neither a mangled line nor an unwritable path
/// costs the caller its turn.
/// </summary>
public class UsageHistoryLogTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _logPath;

    public UsageHistoryLogTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _logPath = Path.Combine(_tempDir, "usage-history.jsonl");
    }

    private UsageHistoryLog _Log() => new(_logPath, NullLogger<UsageHistoryLog>.Instance);

    private static UsageSnapshot _Snapshot(string paneId, int outputTokens = 100, string? runId = null) =>
        new()
        {
            PaneId = paneId,
            StartedAt = new DateTimeOffset(2026, 7, 28, 9, 0, 0, TimeSpan.Zero),
            RecordedAt = new DateTimeOffset(2026, 7, 28, 9, 15, 0, TimeSpan.Zero),
            RunKind = runId is null ? UsageRunKind.Interactive : UsageRunKind.Embedded,
            RunId = runId,
            RunLabel = runId is null ? null : "AC-251 - persist usage",
            ProfileLabel = "raymond",
            Model = "opus",
            InputTokens = 10,
            OutputTokens = outputTokens,
            CacheReadInputTokens = 5_000,
            CacheCreationInputTokens = 20,
            TotalCostUsd = 0.42,
            Turns = 3,
        };

    [Fact]
    public async Task RecordAsync_ThenReadRecentAsync_ReturnsEveryFieldItWasGiven()
    {
        var written = _Snapshot("pane-a", runId: "run-7");

        await _Log().RecordAsync(written);

        // A fresh instance, so this proves the file carried it rather than an in-memory list — the whole point.
        var read = Assert.Single(await _Log().ReadRecentAsync());
        Assert.Equal(written.PaneId, read.PaneId);
        Assert.Equal(written.StartedAt, read.StartedAt);
        Assert.Equal(written.RecordedAt, read.RecordedAt);
        Assert.Equal(UsageRunKind.Embedded, read.RunKind);
        Assert.Equal("run-7", read.RunId);
        Assert.Equal(written.RunLabel, read.RunLabel);
        Assert.Equal("raymond", read.ProfileLabel);
        Assert.Equal("opus", read.Model);
        Assert.Equal(written.InputTokens, read.InputTokens);
        Assert.Equal(written.OutputTokens, read.OutputTokens);
        Assert.Equal(written.CacheReadInputTokens, read.CacheReadInputTokens);
        Assert.Equal(written.CacheCreationInputTokens, read.CacheCreationInputTokens);
        Assert.Equal(written.TotalCostUsd, read.TotalCostUsd);
        Assert.Equal(written.Turns, read.Turns);
    }

    [Fact]
    public async Task RunKind_SurvivesTheRoundTrip_AsItsName_NotAsANumber()
    {
        await _Log().RecordAsync(_Snapshot("pane-a", runId: "run-7"));

        // Written as a name so a renumbered enum cannot silently relabel yesterday's records, and so the file
        // stays readable in an editor — the trail's stated promise.
        var line = await File.ReadAllTextAsync(_logPath);
        Assert.Contains("\"Embedded\"", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadRecentAsync_SeveralSessions_KeepsThemApart_AndGivesTheNewestFirst()
    {
        var log = _Log();
        await log.RecordAsync(_Snapshot("pane-a"));
        await log.RecordAsync(_Snapshot("pane-b"));
        await log.RecordAsync(_Snapshot("pane-c"));

        var read = await log.ReadRecentAsync();

        Assert.Equal(["pane-c", "pane-b", "pane-a"], read.Select(entry => entry.PaneId));
    }

    [Fact]
    public async Task ReadRecentAsync_SeveralRecordsForOneSession_KeepsThemAll_SoTheLatestIsTheTotal()
    {
        var log = _Log();
        await log.RecordAsync(_Snapshot("pane-a", outputTokens: 100));
        await log.RecordAsync(_Snapshot("pane-a", outputTokens: 250));

        var read = await log.ReadRecentAsync();

        // Every turn appends, so a session has a record per turn and the last one carries its running total.
        Assert.Equal(2, read.Count);
        Assert.Equal(250, read[0].OutputTokens);
    }

    [Fact]
    public async Task ReadRecentAsync_ALineThatIsNotJson_SkipsIt_RatherThanLosingTheTrail()
    {
        var log = _Log();
        await log.RecordAsync(_Snapshot("pane-a"));
        await File.AppendAllTextAsync(_logPath, "{ half a line, killed mid-write" + Environment.NewLine);
        await log.RecordAsync(_Snapshot("pane-b"));

        var read = await log.ReadRecentAsync();

        Assert.Equal(["pane-b", "pane-a"], read.Select(entry => entry.PaneId));
    }

    [Fact]
    public async Task RecordAsync_WhenTheTrailCannotBeWritten_DoesNotThrow()
    {
        // A directory where the file should be: the write fails every time. Losing a measurement is bad; taking
        // the session's turn down with it is worse, which is why this is a logged warning and not an exception.
        Directory.CreateDirectory(_logPath);
        var log = _Log();

        await log.RecordAsync(_Snapshot("pane-a"));

        Assert.Empty(await log.ReadRecentAsync());
    }

    [Fact]
    public async Task ReadRecentAsync_NoTrailYet_ReturnsNothing()
    {
        Assert.Empty(await _Log().ReadRecentAsync());
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }
}
