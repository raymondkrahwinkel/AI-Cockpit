using Cockpit.App.Composition;
using Cockpit.App.ViewModels;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1646, layer 2 (thread filing): a turn about a project ends up in that project's thread, prompt and reply both,
/// without the assistant having to remember a second call. General keeps only what is about no project.
/// </summary>
public sealed class AssistantThreadFilingTests
{
    private static readonly ProjectHint[] Projects = [new("p1", "Depot", ["DEP"]), new("p2", "WayMark", ["WM"])];

    [Fact]
    public void APromptTypedInAll_LandsInTheThreadOfWhatItWasAbout_WithItsReply()
    {
        var vm = new SessionViewModel();
        vm.Transcript.Clear();
        vm.Threads = new ThreadLookup(id => Projects.FirstOrDefault(project => project.Id == id)?.Name, _ => null, _ => null)
        {
            Infer = text => ProjectInference.Infer(text, Projects, []),
        };

        // One project: set_thread alone files the prompt with the reply.
        var followUp = _Turn(vm, "is this the new version?", "Yes, that is build 115.", assistant: () => vm.SetTurnThreads(["p1"]));
        Assert.All(followUp, row => Assert.Equal(["p1"], row.ThreadProjectIds));

        // Two projects: tag_prompt splits the prompt, set_thread with both carries the reply into each.
        var split = _Turn(vm, "ship the route and check the map", "On it.", assistant: () =>
        {
            vm.TagLastPrompt([("p1", "ship the route"), ("p2", "check the map")]);
            vm.SetTurnThreads(["p1", "p2"]);
        });
        Assert.All(split, row => Assert.Equal(["p1", "p2"], row.ThreadProjectIds));
        Assert.Equal(2, split[0].ProjectParts?.Count);

        // No set_thread: a ticket key in the reply is enough to file both; a project's name alone is not.
        var byTicket = _Turn(vm, "and the other one?", "DEP-14 is merged too.", assistant: () => { });
        var byName = _Turn(vm, "anything new?", "Depot looks quiet.", assistant: () => { });
        Assert.All(byTicket, row => Assert.Equal(["p1"], row.ThreadProjectIds));
        Assert.All(byName, row => Assert.True(row.IsGeneral));

        vm.ShownThread = SessionViewModel.GeneralThread;
        Assert.Equal(byName, vm.VisibleTranscript.Where(row => row.IsUserRow || row.IsAssistantMarkdown));
    }

    // The prompt filed as the composer files one typed in All, the reply streamed in, the assistant's calls, the end.
    private static TranscriptEntryViewModel[] _Turn(SessionViewModel vm, string prompt, string reply, Action assistant)
    {
        var (ids, sources) = vm.ProjectsForPrompt(prompt);
        var row = new TranscriptEntryViewModel(TranscriptEntryKind.UserText, prompt) { ProjectIds = ids, ProjectSources = sources };
        vm.Transcript.Add(row);
        vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = reply });
        assistant();
        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = reply, IsError = false });
        return [row, vm.Transcript.Last(entry => entry.IsAssistantMarkdown)];
    }
}
