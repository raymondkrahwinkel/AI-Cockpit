using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Tests.Shared;
using NSubstitute;

namespace Cockpit.Core.Tests.Sessions;

// Assistant threads: a thread is open or closed. Closing keeps every row, General and a thread with something waiting
// do not close, new activity for the project opens it again, and the suggestion to close is one row for all of them.
public class ThreadLifecycleTests
{
    private const string Question = """{"questions":[{"question":"Welke?","options":[{"label":"A"},{"label":"B"}]}]}""";

    private static SessionViewModel _Session(Func<string, IReadOnlyList<string>>? sessionsOf = null)
    {
        var vm = TestSessions.Pane(Substitute.For<ISessionManager>());
        vm.Threads = new ThreadLookup(id => id is "depot" or "waymark" ? id : null, _ => null, _ => null)
        {
            SessionsOf = sessionsOf ?? (_ => []),
        };
        vm.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "hoe staat Depot ervoor?") { ProjectIds = ["depot"] });
        vm.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "Groen."));
        return vm;
    }

    [Fact]
    public void ClosingAThread_KeepsItsRows_AndSaysSoOnce()
    {
        var vm = _Session();
        var changes = 0;
        vm.ClosedThreadsChanged += () => changes++;

        Assert.True(vm.CloseThread("depot").Ok);

        Assert.True(vm.IsThreadClosed("depot"));
        Assert.Equal(2, vm.Transcript.Count(row => row.IsInThread("depot")));
        Assert.Equal(vm.Transcript.Count(row => row.IsRowVisible), vm.VisibleTranscript.Count);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void General_AndAThreadWithSomethingWaiting_DoNotClose()
    {
        var vm = _Session();
        vm.AddQuestion("Welke?", Question, ["depot"]);

        Assert.False(vm.CloseThread(SessionViewModel.GeneralThread).Ok);
        Assert.False(vm.CloseThread("depot").Ok);
        Assert.False(vm.IsThreadClosed("depot"));
    }

    [Fact]
    public void NewActivityForTheProject_OpensItsThreadAgain_ButTheAssistantsOwnReplyDoesNot()
    {
        var vm = _Session();
        vm.CloseThread("depot");

        vm.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "Nog iets over Depot."));
        Assert.True(vm.IsThreadClosed("depot"));

        vm.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.Status, "PR #88 gemerged") { ProjectIds = ["depot"], MessageKind = "merged" });
        Assert.False(vm.IsThreadClosed("depot"));

        vm.CloseThread("depot");
        vm.AddQuestion("Welke?", Question, ["depot"]);
        Assert.False(vm.IsThreadClosed("depot"));
    }

    [Fact]
    public void TheSuggestionToClose_IsOneRow_LeavesOutWhatStillRuns_AndTheClickDecides()
    {
        var vm = _Session(sessionsOf: id => id == "waymark" ? ["WM-12 · Busy"] : []);
        vm.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "en WayMark?") { ProjectIds = ["waymark"] });

        var suggested = vm.SuggestClosingThreads(["depot", "waymark"]);

        Assert.True(suggested.Ok);
        Assert.Contains("waymark (a session is still running)", suggested.Note, StringComparison.Ordinal);
        var row = Assert.Single(vm.Transcript, row => row.IsSuggestionRow);
        Assert.Equal(["depot"], row.ThreadProjectIds);

        vm.SuggestClosingThreads(["depot"]);
        Assert.Single(vm.Transcript, row => row.IsSuggestionRow);

        row.AcceptSuggestionCommand.Execute(null);
        Assert.True(vm.IsThreadClosed("depot"));
        Assert.False(row.IsSuggestionOpen);
        Assert.Equal("Closed depot", row.SuggestionText);
    }

    [Fact]
    public void KeepingThem_ClosesNothing()
    {
        var vm = _Session();
        vm.SuggestClosingThreads(["depot"]);
        var row = Assert.Single(vm.Transcript, row => row.IsSuggestionRow);

        row.KeepSuggestionCommand.Execute(null);

        Assert.False(vm.IsThreadClosed("depot"));
        Assert.Equal("Kept open", row.SuggestionText);
    }

    [Fact]
    public void ListThreads_SaysWhatSpeaksForClosingEachOne()
    {
        var vm = _Session(sessionsOf: id => id == "depot" ? ["DEP-422 · Idle"] : []);
        vm.CloseThread("depot");

        var depot = Assert.Single(vm.SummarizeThreads(), thread => thread.ProjectId == "depot");

        Assert.True(depot.IsClosed);
        Assert.Equal(2, depot.Rows);
        Assert.Equal(["DEP-422 · Idle"], depot.RunningSessions);
        Assert.Equal(0, depot.OpenQuestions);
    }
}
