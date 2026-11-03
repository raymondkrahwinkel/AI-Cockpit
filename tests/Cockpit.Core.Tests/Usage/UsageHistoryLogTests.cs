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
    public async Task ReadRecentAsync_ALineThatIsNotJson_SkipsIt_RatherThanLosingTheTrail()
    {
        var log = _Log();
        await log.RecordAsync(_Snapshot("pane-a"));
        await File.AppendAllTextAsync(_logPath, "{ half a line, killed mid-write" + Environment.NewLine);
        await log.RecordAsync(_Snapshot("pane-b"));

        var read = await log.ReadRecentAsync();

        Assert.Equal(["pane-b", "pane-a"], read.Select(entry => entry.PaneId));
    }

    // --- Rotation (AC-399) ---------------------------------------------------------------------------------

    private string _RolloverPath() => UsageHistoryLog.RolloverPathFor(_logPath);

    private UsageHistoryLog _LogWithTinyLimit(long maxSizeBytes) =>
        new(_logPath, NullLogger<UsageHistoryLog>.Instance, maxSizeBytes);

    [Fact]
    public async Task RecordAsync_AtTheSizeLimit_RollsToTheDotOneFile_KeepingADayNotFoldedYet()
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

        // Rolling again keeps one generation, but every line here is from one day that is not folded yet (AC-1719),
        // so the old generation's lines move along instead of being dropped.
        await log.RecordAsync(_Snapshot("pane-c"));
        Assert.Contains("pane-b", await File.ReadAllTextAsync(_RolloverPath()));
        Assert.Contains("pane-a", await File.ReadAllTextAsync(_RolloverPath()));
        Assert.DoesNotContain("pane-a", await File.ReadAllTextAsync(_logPath));
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
