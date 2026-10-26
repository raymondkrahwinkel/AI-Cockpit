using Cockpit.Infrastructure.Usage;
using Cockpit.Plugins.Abstractions.TimeTracking;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Infrastructure.Tests.Usage;

public sealed class CockpitTimeEntriesLogTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
    private readonly string _logPath;

    public CockpitTimeEntriesLogTests()
    {
        Directory.CreateDirectory(_tempDir);
        _logPath = Path.Combine(_tempDir, "own-time-entries.jsonl");
    }

    [Fact]
    public async Task OfferedEntry_IsFrozen_UntilSkipped_ReopensIt_WhileBookedStaysFrozen()
    {
        var log = _Log(Start);
        var entry = await log.AddAsync("Client call", Start, TimeSpan.FromMinutes(15), null, CockpitTimeEntrySource.Duration);

        await log.MarkOfferedAsync([entry.Id], Start.AddHours(1), "pane-agent");

        var updateError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            log.UpdateAsync(entry.Id, "Changed", Start, TimeSpan.FromMinutes(30), null));
        Assert.Contains("offered", updateError.Message, StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<InvalidOperationException>(() => log.DeleteAsync(entry.Id));

        await log.SetOutcomeAsync([entry.Id], CockpitTimeEntryOutcome.Skipped, "Not booked");
        var changed = await log.UpdateAsync(entry.Id, "Client call updated", Start, TimeSpan.FromMinutes(30), "project-1");
        Assert.Equal(CockpitTimeEntryStatus.Open, changed.Status);

        await log.MarkOfferedAsync([entry.Id], Start.AddHours(2), "pane-agent");
        await log.SetOutcomeAsync([entry.Id], CockpitTimeEntryOutcome.Booked);

        await Assert.ThrowsAsync<InvalidOperationException>(() => log.DeleteAsync(entry.Id));
        var persisted = Assert.Single(await _Log(Start.AddHours(3)).ListAsync(Start.Date, Start.Date.AddDays(1)));
        Assert.Equal(CockpitTimeEntryStatus.Booked, persisted.Status);
        Assert.Equal("Client call updated", persisted.Description);

        var blockedPath = Path.Combine(_tempDir, "path-is-a-directory");
        Directory.CreateDirectory(blockedPath);
        var blockedLog = new CockpitTimeEntriesLog(
            blockedPath,
            NullLogger<CockpitTimeEntriesLog>.Instance,
            new FixedTimeProvider(Start));
        Assert.NotNull(await Record.ExceptionAsync(() => blockedLog.AddAsync(
            "Must not look saved",
            Start,
            TimeSpan.FromMinutes(15),
            null,
            CockpitTimeEntrySource.Duration)));
    }

    [Fact]
    public async Task Stopwatch_SurvivesAHostRestart_AndASecondStartIsRefused()
    {
        var firstHost = _Log(Start);
        var started = await firstHost.StartStopwatchAsync("Review", "project-1");

        var restartedHost = _Log(Start.AddMinutes(25));
        var running = await restartedHost.GetRunningStopwatchAsync();
        Assert.Equal(started, running);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            restartedHost.StartStopwatchAsync("Another task", null));
        Assert.Contains("already running", error.Message, StringComparison.OrdinalIgnoreCase);

        var stopped = await restartedHost.StopStopwatchAsync();

        Assert.Equal(TimeSpan.FromMinutes(25), stopped.Duration);
        Assert.Equal(CockpitTimeEntrySource.Stopwatch, stopped.Source);
        Assert.Null(await _Log(Start.AddMinutes(30)).GetRunningStopwatchAsync());
        Assert.Equal(stopped, Assert.Single(await _Log(Start.AddMinutes(30)).ListAsync(Start.Date, Start.Date.AddDays(1))));
    }

    private CockpitTimeEntriesLog _Log(DateTimeOffset now) =>
        new(_logPath, NullLogger<CockpitTimeEntriesLog>.Instance, new FixedTimeProvider(now));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
