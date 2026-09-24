using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

// AC-1377: the host forms and records the rows, the view draws them. The seams where that goes wrong are a row the
// view formed itself coming back as the host's echo, a drawn change recorded back, and a view-side change the host
// then overwrites.
public class SessionViewModelHostRowsTests
{
    // The user echo is formed here and recorded through the host, which raises it straight back as an upsert.
    [Fact]
    public void ARowTheViewFormedItself_IsOneRow_WhenTheHostEchoesIt()
    {
        var vm = new SessionViewModel(Substitute.For<ISessionManager>());

        vm.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "hello"));

        Assert.Single(vm.Transcript);
    }

    // Drawing an upsert sets a row's fields one at a time; recorded back halfway, the store would get a version of a
    // failed call that has its error flag but not yet its result.
    [Fact]
    public void AnUpsertDrawnOntoItsRow_IsWrittenOnce_NotRecordedBackWhileItIsDrawn()
    {
        var store = new RecordingStore();
        var vm = new SessionViewModel(Substitute.For<ISessionManager>(), transcriptStore: store);

        vm.Apply(new ToolUseRequested { SessionId = "S", ToolUseId = "t1", ToolName = "Bash", InputJson = "{}" });
        vm.Apply(new ToolResult { SessionId = "S", ToolUseId = "t1", Content = "no such file", IsError = true });

        Assert.Equal([null, "no such file"], store.Appended.Select(entry => entry.ResultText));
    }

    // A decision made here lands on the host's row, so the host's next version of that row — the result arriving —
    // carries it too, on screen and in the store.
    [Fact]
    public void ADecisionMadeInTheView_ReachesTheStore_AndSurvivesTheHostsNextVersionOfTheRow()
    {
        var store = new RecordingStore();
        var vm = new SessionViewModel(Substitute.For<ISessionManager>(), transcriptStore: store);
        vm.Apply(new ToolUseRequested { SessionId = "S", ToolUseId = "t1", ToolName = "Bash", InputJson = "{}" });
        vm.Apply(new PermissionRequested { SessionId = "S", ToolUseId = "t1", ToolName = "Bash", InputJson = "{}" });
        var row = Assert.Single(vm.Transcript);

        row.PermissionDecision = "Allowed";
        row.IsPendingPermission = false;
        vm.Apply(new ToolResult { SessionId = "S", ToolUseId = "t1", Content = "done", IsError = false });

        Assert.Equal(("Allowed", false, "done"), (row.PermissionDecision, row.IsPendingPermission, row.ResultText));
        Assert.Equal(("Allowed", false, "done"), (store.Appended[^1].PermissionDecision, store.Appended[^1].IsPendingPermission, store.Appended[^1].ResultText));
    }

    private sealed class RecordingStore : ISessionTranscriptStore
    {
        public List<TranscriptSnapshotEntry> Appended { get; } = [];

        public Task AppendAsync(string paneId, TranscriptSnapshotEntry entry, CancellationToken cancellationToken = default)
        {
            Appended.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TranscriptSnapshotEntry>?> TryLoadAsync(string paneId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TranscriptSnapshotEntry>?>([]);

        public Task ArchiveAsync(string paneId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
