using System.Text.Json;
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

    // --- Rotation (AC-399) ---------------------------------------------------------------------------------

    private string _RolloverPath() => UsageHistoryLog.RolloverPathFor(_logPath);

    private UsageHistoryLog _LogWithTinyLimit(long maxSizeBytes) =>
        new(_logPath, NullLogger<UsageHistoryLog>.Instance, maxSizeBytes);

    [Fact]
    public async Task RecordAsync_UnderTheSizeLimit_NeverRolls()
    {
        // The mutation-style boundary check (AC5): with the limit far above what a handful of records could ever
        // reach, no rollover file must appear. If the size check were deleted or always-true/always-false in the
        // wrong direction, this — or the test below it — goes red.
        var log = _LogWithTinyLimit(maxSizeBytes: 10 * 1024 * 1024);

        for (var i = 0; i < 5; i++)
        {
            await log.RecordAsync(_Snapshot($"pane-{i}"));
        }

        Assert.False(File.Exists(_RolloverPath()));
    }

    [Fact]
    public async Task RecordAsync_AtTheSizeLimit_RollsToTheDotOneFile_DroppingWhatWasThereBefore()
    {
        // A tiny limit that the very first written line already exceeds, so the *second* write is the one that
        // finds the file over limit and rolls it. Proves AC1 (bounded growth via a single rollover generation)
        // and, combined with the test above, the boundary condition itself (AC5): remove or invert the size
        // check and one of these two tests fails.
        var log = _LogWithTinyLimit(maxSizeBytes: 1);

        await log.RecordAsync(_Snapshot("pane-a"));
        var firstLine = await File.ReadAllTextAsync(_logPath);

        await log.RecordAsync(_Snapshot("pane-b"));

        Assert.True(File.Exists(_RolloverPath()));
        Assert.Equal(firstLine, await File.ReadAllTextAsync(_RolloverPath()));
        Assert.Contains("pane-b", await File.ReadAllTextAsync(_logPath));
        Assert.DoesNotContain("pane-a", await File.ReadAllTextAsync(_logPath));

        // Rolling again overwrites the previous rollover generation rather than chaining a second one.
        await log.RecordAsync(_Snapshot("pane-c"));
        Assert.Contains("pane-b", await File.ReadAllTextAsync(_RolloverPath()));
        Assert.DoesNotContain("pane-a", await File.ReadAllTextAsync(_RolloverPath()));
    }

    [Fact]
    public async Task ReadRecentAsync_AfterARollover_ContinuesIntoTheRolloverFile()
    {
        // AC3: a tail-read that exhausts the live file must not stop right where the rollover happened — "recent
        // usage history" should read across the boundary rather than appearing to drop to nothing.
        var log = _LogWithTinyLimit(maxSizeBytes: 1);
        await log.RecordAsync(_Snapshot("pane-old")); // rolls to .1.jsonl on the next write
        await log.RecordAsync(_Snapshot("pane-new")); // lives in the current file

        var recent = await log.ReadRecentAsync();

        Assert.Equal(["pane-new", "pane-old"], recent.Select(entry => entry.PaneId));
    }

    [Fact]
    public async Task ReadRecentAsync_LimitSatisfiedByTheLiveFileAlone_DoesNotTouchTheRolloverFile()
    {
        // The rollover file is only consulted when the live file did not already fill the request — the common
        // case (no rollover yet, or a limit smaller than the live file holds) should not pay for reading it.
        var log = _LogWithTinyLimit(maxSizeBytes: 1);
        await log.RecordAsync(_Snapshot("pane-old"));
        await log.RecordAsync(_Snapshot("pane-new"));

        var recent = await log.ReadRecentAsync(limit: 1);

        Assert.Equal(["pane-new"], recent.Select(entry => entry.PaneId));
    }

    [Fact]
    public async Task RecordAsync_ManySmallWritesAcrossATinyLimit_LosesNoRecord_AndEveryLineStaysWellFormed()
    {
        // AC4: rolling during ongoing writes must not corrupt a line or lose a record. A tiny limit forces several
        // rollovers across many sequential writes; every record must still be recoverable afterwards from the two
        // files combined (the live one plus whatever generation is left in the rollover file).
        var log = _LogWithTinyLimit(maxSizeBytes: 500);
        const int total = 200;

        for (var i = 0; i < total; i++)
        {
            await log.RecordAsync(_Snapshot($"pane-{i}"));
        }

        foreach (var path in new[] { _logPath, _RolloverPath() })
        {
            if (!File.Exists(path))
            {
                continue;
            }

            foreach (var line in await File.ReadAllLinesAsync(path))
            {
                if (line.Length == 0)
                {
                    continue;
                }

                // Throws if a line was truncated or interleaved by a rollover racing a write.
                JsonDocument.Parse(line).Dispose();
            }
        }

        // The live file always holds at least the very last write — a rollover happening does not lose the
        // record that triggered it.
        Assert.Contains($"pane-{total - 1}", await File.ReadAllTextAsync(_logPath));
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
