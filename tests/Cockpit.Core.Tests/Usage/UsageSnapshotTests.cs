using Cockpit.Core.Usage;

namespace Cockpit.Core.Tests.Usage;

/// <summary>The two figures a usage snapshot works out for itself (AC-251) rather than storing twice.</summary>
public class UsageSnapshotTests
{
    [Fact]
    public void TotalTokens_CountsEveryBucket_IncludingTheCacheOnes()
    {
        var snapshot = new UsageSnapshot
        {
            InputTokens = 1_000,
            OutputTokens = 200,
            CacheReadInputTokens = 40_000,
            CacheCreationInputTokens = 3_000,
        };

        // The cache buckets are the bulk of a long session's spend — leaving either out understates it by an order
        // of magnitude, which is the opposite of what a baseline is for.
        Assert.Equal(44_200, snapshot.TotalTokens);
    }

    [Fact]
    public void Duration_RunsFromTheSessionStart_ToTheMomentTheRecordWasWritten()
    {
        var started = new DateTimeOffset(2026, 7, 28, 9, 0, 0, TimeSpan.FromHours(2));

        var snapshot = new UsageSnapshot
        {
            StartedAt = started,
            RecordedAt = started.AddMinutes(37),
        };

        Assert.Equal(TimeSpan.FromMinutes(37), snapshot.Duration);
    }

    [Fact]
    public void Duration_AcrossOffsets_MeasuresRealElapsedTime_NotTheClockFaces()
    {
        // A run started before a machine changed timezone (or a record written by a session on another offset) must
        // not read as an hour longer than it was: DateTimeOffset subtraction is absolute, and this pins that.
        var started = new DateTimeOffset(2026, 7, 28, 9, 0, 0, TimeSpan.FromHours(2));
        var recorded = new DateTimeOffset(2026, 7, 28, 8, 30, 0, TimeSpan.FromHours(1));

        var snapshot = new UsageSnapshot { StartedAt = started, RecordedAt = recorded };

        Assert.Equal(TimeSpan.FromMinutes(30), snapshot.Duration);
    }
}
