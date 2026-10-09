using System.Text.Json;
using Cockpit.App.Composition;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Core.Tests.Sessions;

// AC-1518, layer 2 (an algorithm): every step of a sub-agent published and wrote its whole lane again, inside the
// anchor row, so a lane of n steps cost n² — measured live as the UI thread serialising the lane per step. A lane four
// times as long has to cost about four times as much, and still come back whole from what was written.
public sealed class Ac1518_SubAgentLaneScaleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task ALaneFourTimesAsLong_PublishesAboutFourTimesAsMuch_AndRestoresWhole()
    {
        var (shortBytes, _) = await _PlayLaneAsync(steps: 100);
        var (longBytes, restored) = await _PlayLaneAsync(steps: 400);

        // Linear is a ratio of 4, quadratic 16.
        var ratio = (double)longBytes / shortBytes;
        Assert.True(ratio < 6, $"400 steps published {longBytes} bytes, 100 steps {shortBytes}: {ratio:F1}x");

        var anchor = Assert.Single(restored, row => row.ToolUseId == "agent");
        Assert.Equal(Enumerable.Range(0, 400).Select(step => $"done {step}"), anchor.SubAgentRows.Select(row => row.ResultText));
    }

    // Bytes as every upsert serialises: what the event log appends and the store writes, once per row version.
    private async Task<(long Bytes, IReadOnlyList<TranscriptEntryViewModel> Restored)> _PlayLaneAsync(int steps)
    {
        var log = new SessionTranscriptLog(_root, NullLogger<SessionTranscriptLog>.Instance, TimeSpan.FromHours(1));
        var vm = TestSessions.Pane(Substitute.For<ISessionManager>(), transcriptStore: log);
        var bytes = 0L;
        vm.Control.RowUpserted += upsert => bytes += JsonSerializer.SerializeToUtf8Bytes(upsert.Row).Length;
        vm.Apply(new ToolUseRequested { SessionId = "S", ToolUseId = "agent", ToolName = "Agent", InputJson = "{}" });
        for (var step = 0; step < steps; step++)
        {
            vm.Apply(new ToolUseRequested { SessionId = "S", ParentToolUseId = "agent", ToolUseId = $"r{step}", ToolName = "Read", InputJson = "{}" });
            vm.Apply(new ToolResult { SessionId = "S", ParentToolUseId = "agent", ToolUseId = $"r{step}", Content = $"done {step}", IsError = false });
        }

        await log.DisposeAsync();
        var recorded = await new SessionTranscriptLog(_root, NullLogger<SessionTranscriptLog>.Instance).TryLoadAsync(vm.PaneId);
        Assert.NotNull(recorded);
        return (bytes, TranscriptSnapshot.Restore(recorded));
    }
}
