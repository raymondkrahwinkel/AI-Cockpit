using Cockpit.App.Services;
using Cockpit.Core.Abstractions.Diagnostics;
using Cockpit.Core.Diagnostics;

namespace Cockpit.App.ViewTests;

/// <summary>
/// Assembling the diagnostics snapshot (AC-58). The figures the tester copies must agree with the status bar, so a
/// session is weighed as its whole process tree — the claude process plus what it spawned — and a session with no
/// local process contributes nothing rather than a misleading zero-that-looks-like-idle. The OS crash artifacts
/// pass straight through, and the live platform/memory/heap sections are actually populated.
/// </summary>
public class DiagnosticsCollectorTests
{
    [Fact]
    public void Collect_WeighsEachSessionAsItsWholeProcessTree()
    {
        var rows = new List<ProcessRow>
        {
            new(100, 1, TimeSpan.Zero, 50_000_000, "claude"),
            new(101, 100, TimeSpan.Zero, 30_000_000, "node"),
        };
        var collector = new DiagnosticsCollector(new FakeProcessTable(rows), new FakeCrashLogReader([]));

        var snapshot = collector.Collect([
            new SessionDescriptor("agent", "Agent", 100),
            new SessionDescriptor("http-only", "Agent", null),
        ]);

        Assert.Equal(2, System.Linq.Enumerable.Count(snapshot.Sessions));
        Assert.Equal(80_000_000, snapshot.Sessions[0].ResidentBytes);
        Assert.Equal(100, snapshot.Sessions[0].ProcessId);
        Assert.Equal(0, snapshot.Sessions[1].ResidentBytes);
        Assert.Null(snapshot.Sessions[1].ProcessId);
    }

    [Fact]
    public void Collect_PassesTheOsCrashArtifactsThrough()
    {
        var crash = new CrashLogEntry("Linux OOM (kernel log)", string.Empty, null, "Killed process 100 (Cockpit.App)");
        var collector = new DiagnosticsCollector(new FakeProcessTable([]), new FakeCrashLogReader([crash]));

        var snapshot = collector.Collect([]);

        Assert.Equal(crash, Assert.Single(snapshot.CrashLogs));
    }

    [Fact]
    public void Collect_PopulatesTheLivePlatformAndMemorySections()
    {
        var collector = new DiagnosticsCollector(new FakeProcessTable([]), new FakeCrashLogReader([]));

        var snapshot = collector.Collect([]);

        Assert.False(string.IsNullOrWhiteSpace(snapshot.Platform.RuntimeVersion));
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Platform.AvaloniaVersion));
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Rendering.Mode));
        Assert.True(snapshot.Memory.ResidentBytes > 0);
        Assert.True(snapshot.ManagedHeap.HeapSizeBytes >= 0);
    }

    private sealed class FakeProcessTable(IReadOnlyList<ProcessRow> rows) : IProcessTableReader
    {
        public IReadOnlyList<ProcessRow> Read() => rows;
    }

    private sealed class FakeCrashLogReader(IReadOnlyList<CrashLogEntry> entries) : ICrashLogReader
    {
        public IReadOnlyList<CrashLogEntry> RecentEntries(int max) => entries;
    }
}
