using System.Collections.Specialized;
using Cockpit.App.Composition;
using Cockpit.App.ViewModels;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1646, layer 2 (thread filing): what a turn is about files it, prompt and reply both, wherever it was typed and
/// without the assistant having to remember a second call; nothing leaves the thread it was typed in. General keeps
/// only what is about no project, and what was typed there.
/// </summary>
public sealed class AssistantThreadFilingTests
{
    private static readonly ProjectHint[] Projects = [new("p1", "Depot", ["DEP"]), new("p2", "WayMark", ["WM"])];

    [Fact]
    public void ATurnLandsInTheThreadOfWhatItWasAbout_AndStaysWhereItWasTyped()
    {
        var vm = new SessionViewModel();
        vm.Transcript.Clear();
        vm.Threads = new ThreadLookup(id => Projects.FirstOrDefault(project => project.Id == id)?.Name, _ => null, _ => null)
        {
            Infer = text => ProjectInference.Infer(text, Projects, []),
        };

        // Refiling a row the open thread shows reconciles what is shown, never resets it (AC-1644).
        var resets = 0;
        vm.VisibleTranscript.CollectionChanged += (_, e) => resets += e.Action == NotifyCollectionChangedAction.Reset ? 1 : 0;

        // One project: set_thread alone files the prompt with the reply, from All, from General, from another thread.
        var fromAll = _Turn(vm, null, "is this the new version?", "Yes, that is build 115.", assistant: () => vm.SetTurnThreads(["p1"]));
        var fromGeneral = _Turn(vm, SessionViewModel.GeneralThread, "can you update Depot?", "Updating.", assistant: () => vm.SetTurnThreads(["p1"]));
        var fromWayMark = _Turn(vm, "p2", "and Depot too?", "Depot as well.", assistant: () => vm.SetTurnThreads(["p1"]));
        Assert.All(fromAll, row => Assert.Equal(("p1", false), _Filed(row)));
        Assert.All(fromGeneral, row => Assert.Equal(("p1", true), _Filed(row)));
        Assert.All(fromWayMark, row => Assert.Equal(("p1,p2", false), _Filed(row)));

        // Two projects: tag_prompt splits the prompt, set_thread with both carries the reply into each.
        var split = _Turn(vm, null, "ship the route and check the map", "On it.", assistant: () =>
        {
            vm.TagLastPrompt([("p1", "ship the route"), ("p2", "check the map")]);
            vm.SetTurnThreads(["p1", "p2"]);
        });
        Assert.All(split, row => Assert.Equal(["p1", "p2"], row.ThreadProjectIds));
        Assert.Equal(2, split[0].ProjectParts?.Count);

        // No set_thread: a ticket key in the reply files both, here from General; a project's name alone does not.
        var byTicket = _Turn(vm, SessionViewModel.GeneralThread, "and the other one?", "DEP-14 is merged too.", assistant: () => { });
        var byName = _Turn(vm, null, "anything new?", "Depot looks quiet.", assistant: () => { });
        Assert.All(byTicket, row => Assert.Equal(("p1", true), _Filed(row)));
        Assert.All(byName, row => Assert.Equal(("", true), _Filed(row)));
        Assert.Equal(0, resets);

        vm.ShownThread = SessionViewModel.GeneralThread;
        Assert.Equal([.. fromGeneral, .. byTicket, .. byName], _Conversation(vm));
        vm.ShownThread = "p2";
        Assert.Equal([.. fromWayMark, .. split], _Conversation(vm));
        vm.ShownThread = null;
        Assert.Equal(vm.Transcript.Where(row => row.IsUserRow || row.IsAssistantMarkdown), _Conversation(vm));
    }

    // The prompt filed as the composer files one typed in that thread, the reply streamed in, the assistant's calls, the end.
    private static TranscriptEntryViewModel[] _Turn(SessionViewModel vm, string? typedIn, string prompt, string reply, Action assistant)
    {
        vm.ShownThread = typedIn;
        var (ids, sources) = vm.ProjectsForPrompt(prompt);
        var row = new TranscriptEntryViewModel(TranscriptEntryKind.UserText, prompt) { ProjectIds = ids, ProjectSources = sources };
        vm.Transcript.Add(row);
        vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = reply });
        assistant();
        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = reply, IsError = false });
        var turn = new[] { row, vm.Transcript.Last(entry => entry.IsAssistantMarkdown) };
        Assert.All(turn, entry => Assert.Contains(entry, vm.VisibleTranscript));
        return turn;
    }

    // Its projects, and whether General shows it too.
    private static (string Projects, bool InGeneral) _Filed(TranscriptEntryViewModel row) =>
        (string.Join(",", row.ThreadProjectIds), row.IsGeneral);

    private static List<TranscriptEntryViewModel> _Conversation(SessionViewModel vm) =>
        [.. vm.VisibleTranscript.Where(row => row.IsUserRow || row.IsAssistantMarkdown)];
}
