using Cockpit.App.ViewModels;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// A row the reading level hides used to stay an item in the transcript's <c>VirtualizingStackPanel</c>, rendering
/// at zero height. The panel then had to realise roughly ten items to fill one viewport's worth of visible rows, and
/// each realisation builds a whole <c>TranscriptRowView</c>. Measured headless at 1000×700 over 600 rows with one in
/// ten visible: 71 realised rows and 121 MiB live against Developer's 15 rows and 41 MiB, at 123–136 ms per wheel
/// click against 36–58 ms. So the views bind to <see cref="SessionViewModel.VisibleTranscript"/> instead, and this
/// fixes what that collection must contain — it is the transcript the operator actually sees, in transcript order,
/// which is the whole claim the views now rest on.
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

    /// <summary>Two or more consecutive auto tool calls are what forms a fold group, and the group is what hides rows.</summary>
    private static void _AddRun(SessionViewModel vm, int calls)
    {
        for (var i = 0; i < calls; i++)
        {
            vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = $"t{Guid.NewGuid()}", ToolName = "Bash", InputJson = "{}" });
        }
    }

    [Fact]
    public void AtDeveloper_EveryRowIsVisible()
    {
        var vm = _Session(ReadingLevel.Developer);
        _Add(vm, TranscriptEntryKind.AssistantText, "prose");
        _Add(vm, TranscriptEntryKind.Thinking, "reasoning");
        _AddRun(vm, 4);

        Assert.Equal(vm.Transcript, vm.VisibleTranscript);
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
        // Simple hides auto tool calls outright and keeps prose; Thinking stays hidden below Developer.
        Assert.Equal([vm.Transcript[0]], vm.VisibleTranscript);

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
