using System.Collections.Specialized;
using Cockpit.App.ViewModels;
using Cockpit.Core.Sessions;
using Cockpit.App.Composition;

namespace Cockpit.App.ViewTests;

/// <summary>
/// What <see cref="SessionViewModel.VisibleTranscript"/> must hold: the rows the operator sees, in transcript
/// order. That is the claim both transcript views rest on since AC-800.
/// </summary>
public sealed class VisibleTranscriptTests
{
    private static SessionViewModel _Session(ReadingLevel level)
    {
        var vm = new SessionViewModel();
        vm.Transcript.Clear();
        vm.ReadingLevel = level;
        return vm;
    }

    private static TranscriptEntryViewModel _Add(SessionViewModel vm, TranscriptEntryKind kind, string text)
    {
        var entry = new TranscriptEntryViewModel(kind, text);
        vm.Transcript.Add(entry);
        return entry;
    }

    /// <summary>
    /// Two or more consecutive auto tool calls are what forms a fold group, and the group is what hides rows.
    /// </summary>
    private static void _AddRun(SessionViewModel vm, int calls)
    {
        for (var i = 0; i < calls; i++)
        {
            vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = $"t{Guid.NewGuid()}", ToolName = "Bash", InputJson = "{}" });
        }
    }

    /// <summary>
    /// AC-1644, layer 2 (scroll stability): a reply that names its project refiles on every delta, and in a thread
    /// that refile used to Clear and refill this list. The panel answers a Reset by forgetting every row height and
    /// container — measured as the newest row sinking 1287px below the viewport over one streamed reply.
    /// </summary>
    [Fact]
    public void AReplyStreamingIntoTheOpenThread_NeverResetsWhatIsShown()
    {
        var vm = _Session(ReadingLevel.Developer);
        vm.Threads = new ThreadLookup(id => id == "p1" ? "P1" : null, _ => null, _ => null);
        vm.ShownThread = "p1";
        _Add(vm, TranscriptEntryKind.UserText, "do it in P1").ProjectIds = ["p1"];

        var resets = 0;
        vm.VisibleTranscript.CollectionChanged += (_, e) => resets += e.Action == NotifyCollectionChangedAction.Reset ? 1 : 0;
        vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = "<!-- project:p1 -->\nstart" });
        for (var i = 0; i < 5; i++)
        {
            vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = $" word {i}" });
        }

        Assert.Equal(0, resets);
        Assert.Equal(vm.Transcript, vm.VisibleTranscript);

        // Without the Reset the list is walked into shape instead, so leaving the thread and coming back must still
        // land on exactly the rows each view holds.
        var general = new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "nothing to do with P1") { ProjectIds = [] };
        vm.ShownThread = null;
        vm.Transcript.Add(general);
        Assert.Equal(vm.Transcript, vm.VisibleTranscript);

        vm.ShownThread = "p1";
        Assert.Equal(vm.Transcript.Where(row => !ReferenceEquals(row, general)), vm.VisibleTranscript);
    }

    [Fact]
    public void AtFocus_HiddenRowsAreNotItems_AndTheRestKeepTranscriptOrder()
    {
        var vm = _Session(ReadingLevel.Focus);
        var prose = _Add(vm, TranscriptEntryKind.AssistantText, "prose");
        _Add(vm, TranscriptEntryKind.Thinking, "reasoning");
        _AddRun(vm, 4);
        var closing = _Add(vm, TranscriptEntryKind.AssistantText, "closing");

        // Thinking is hidden at Focus, and a run of four folds to its anchor alone.
        var anchor = vm.Transcript.Single(row => row.IsGroupAnchor);
        Assert.Equal([prose, anchor, closing], vm.VisibleTranscript);
    }

    [Fact]
    public void ExpandingAFoldedRun_PutsItsStepsBackInPlace()
    {
        var vm = _Session(ReadingLevel.Focus);
        var prose = _Add(vm, TranscriptEntryKind.AssistantText, "prose");
        _AddRun(vm, 4);
        var closing = _Add(vm, TranscriptEntryKind.AssistantText, "closing");

        var anchor = vm.Transcript.Single(row => row.IsGroupAnchor);
        anchor.GroupToggleRequested!();

        // The steps come back between the prose and the closing line — the order the operator reads, not appended
        // at the end, which is the failure mode a naive re-sync would give.
        Assert.Equal(vm.Transcript, vm.VisibleTranscript);
        Assert.Equal(prose, vm.VisibleTranscript[0]);
        Assert.Equal(closing, vm.VisibleTranscript[^1]);

        anchor.GroupToggleRequested!();
        Assert.Equal([prose, anchor, closing], vm.VisibleTranscript);
    }

    [Fact]
    public void SwitchingReadingLevel_RebuildsWhatIsShown()
    {
        var vm = _Session(ReadingLevel.Focus);
        _Add(vm, TranscriptEntryKind.AssistantText, "prose");
        _Add(vm, TranscriptEntryKind.Thinking, "reasoning");
        _AddRun(vm, 4);

        Assert.Equal(2, vm.VisibleTranscript.Count);

        vm.ReadingLevel = ReadingLevel.Developer;
        Assert.Equal(vm.Transcript, vm.VisibleTranscript);

        vm.ReadingLevel = ReadingLevel.Simple;
        // Simple shows each command as its own step (AC-1530) and keeps prose; Thinking stays hidden below Developer.
        Assert.Equal(vm.Transcript.Where(row => !row.IsThinking), vm.VisibleTranscript);

        vm.ReadingLevel = ReadingLevel.Focus;
        Assert.Equal(2, vm.VisibleTranscript.Count);
    }

    [Fact]
    public void AToolCallThatAsksForConsent_BecomesVisibleWhereItStands()
    {
        var vm = _Session(ReadingLevel.Focus);
        var prose = _Add(vm, TranscriptEntryKind.AssistantText, "prose");
        _AddRun(vm, 4);
        var closing = _Add(vm, TranscriptEntryKind.AssistantText, "closing");

        // A tool row arrives as auto and can turn into a consent row a beat later, which pulls it out of the fold.
        var third = vm.Transcript[3];
        third.IsPendingPermission = true;

        Assert.Contains(third, vm.VisibleTranscript);
        Assert.Equal(
            vm.Transcript.Where(row => row.IsRowVisible),
            vm.VisibleTranscript);
        Assert.Equal(prose, vm.VisibleTranscript[0]);
        Assert.Equal(closing, vm.VisibleTranscript[^1]);
    }

    [Fact]
    public void ClearingTheTranscript_ClearsWhatIsShown()
    {
        var vm = _Session(ReadingLevel.Focus);
        _Add(vm, TranscriptEntryKind.AssistantText, "prose");
        _AddRun(vm, 4);

        vm.Transcript.Clear();

        Assert.Empty(vm.VisibleTranscript);
    }
}
