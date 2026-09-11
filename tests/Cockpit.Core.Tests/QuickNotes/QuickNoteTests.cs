using Cockpit.App.Plugins;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Projects;
using Cockpit.Core.Tests.Hotkeys;
using Cockpit.Core.Tests.Voice;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Core.Tests.QuickNotes;

/// <summary>
/// The quick note (AC-492): a note into a project's memory without a session. One counter-proof per acceptance
/// criterion — which projects are offered, that Save starts nothing, that a failed write loses nothing (not even
/// across the window closing), and that "save and start" is the only thing that starts.
/// </summary>
public class QuickNoteTests
{
    private static readonly Project Recent = new("recent", "Recent") { MemoryRef = "depot:recent", LastOpenedAt = DateTimeOffset.Now.AddHours(-1) };
    private static readonly Project Older = new("older", "Older") { MemoryRef = "depot:older", LastOpenedAt = DateTimeOffset.Now.AddDays(-2) };

    [Fact]
    public async Task OnlyProjectsWhoseMemoryTakesANote_AreOffered_MostRecentFirst()
    {
        // The newest project has a bare folder as memory: nothing can write there in v1, so offering it would let
        // the note land nowhere. It is left out rather than shown greyed, and the rest keep the projects list's order.
        var folderOnly = new Project("folder", "Folder") { MemoryRef = @"C:\notes", LastOpenedAt = DateTimeOffset.Now };
        var coordinator = await _CoordinatorAsync(new FakeNoteWriter("recent", "older"), folderOnly, Older, Recent);

        var note = coordinator.CreateViewModel();

        Assert.Equal(["Recent", "Older"], note.Projects.Select(project => project.Name));
        Assert.Same(note.Projects[0], note.SelectedProject);
    }

    [Fact]
    public async Task Save_WritesTheNote_StartsNothing_AndLeavesNoDraftBehind()
    {
        var writer = new FakeNoteWriter("recent");
        var started = new List<Project>();
        var note = new QuickNoteViewModel([Recent], writer, (project, _) => { started.Add(project); return Task.CompletedTask; }) { Note = "call finance" };
        var saved = 0;
        note.Saved += (_, _) => saved++;

        await note.SaveCommand.ExecuteAsync(null);

        Assert.Equal([(Recent, "call finance")], writer.Appended);
        Assert.Empty(started);
        Assert.Equal(1, saved);
        // What landed is no longer a draft, so the next window opens empty rather than offering it a second time.
        Assert.Equal(string.Empty, note.Note);
    }

    /// <summary>The one hard rule: a write that does not land leaves the note in the box and says why, never starts a session on it, and survives Escape — the next window opens on the same text.</summary>
    [Theory]
    [InlineData(ProjectMemoryAppendOutcome.Failed, false, "Depot is unreachable")]
    [InlineData(ProjectMemoryAppendOutcome.AuthorizationRequired, false, "sign-in")]
    [InlineData(ProjectMemoryAppendOutcome.Failed, true, "kept here")]
    public async Task AFailedWrite_KeepsTheNote_SaysWhy_AndStartsNothing(ProjectMemoryAppendOutcome outcome, bool viaSaveAndStart, string expectedFragment)
    {
        var writer = new FakeNoteWriter("recent") { Result = new ProjectMemoryAppendResult(outcome, "Depot is unreachable") };
        var coordinator = await _CoordinatorAsync(writer, Recent);
        var started = 0;
        coordinator.UseStart((_, _) => { started++; return Task.CompletedTask; });
        var note = coordinator.CreateViewModel();
        note.Note = "call finance";
        var saved = 0;
        note.Saved += (_, _) => saved++;

        await (viaSaveAndStart ? note.SaveAndStartCommand : note.SaveCommand).ExecuteAsync(null);

        Assert.Equal("call finance", note.Note);
        Assert.Contains(expectedFragment, note.Message);
        Assert.Equal(0, started);
        Assert.Equal(0, saved);

        // The window closes on Escape and the coordinator is told; the next press opens on what was typed.
        coordinator.KeepDraft(note);
        Assert.Equal("call finance", coordinator.CreateViewModel().Note);
    }

    [Fact]
    public async Task SaveAndStart_StartsOnce_OnlyAfterTheNoteLanded()
    {
        var writer = new FakeNoteWriter("recent");
        var startedAfterWrite = new List<bool>();
        var note = new QuickNoteViewModel([Recent], writer, (_, _) => { startedAfterWrite.Add(writer.Appended.Count == 1); return Task.CompletedTask; }) { Note = "call finance" };

        await note.SaveAndStartCommand.ExecuteAsync(null);

        Assert.Equal([true], startedAfterWrite);
    }

    // A coordinator over a cockpit that holds exactly `projects`.
    private static async Task<QuickNoteCoordinator> _CoordinatorAsync(FakeNoteWriter writer, params Project[] projects)
    {
        var cockpit = TestCockpit.NewViewModel();
        foreach (var project in projects)
        {
            await cockpit.Projects.AddNewProjectAsync(project);
        }

        return new QuickNoteCoordinator(TestGlobalHotkeys.Coordinator(new FakeGlobalHotkeyService()), cockpit, writer);
    }

    private sealed class FakeNoteWriter(params string[] writableProjectIds) : IProjectMemoryNoteWriter
    {
        public ProjectMemoryAppendResult Result { get; init; } = ProjectMemoryAppendResult.Success;

        public List<(Project Project, string Note)> Appended { get; } = [];

        public bool CanAppend(Project project) => writableProjectIds.Contains(project.Id);

        public Task<ProjectMemoryAppendResult> AppendAsync(Project project, string note, CancellationToken cancellationToken)
        {
            Appended.Add((project, note));
            return Task.FromResult(Result);
        }
    }
}
