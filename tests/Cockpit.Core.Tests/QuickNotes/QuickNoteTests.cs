using Cockpit.Infrastructure.Plugins;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Projects;
using Cockpit.Core.Tests.Hotkeys;
using Cockpit.Core.Tests.Voice;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Core.Tests.QuickNotes;

// AC-492: one counter-proof per acceptance criterion; "save and start" is the only thing that starts a session.
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
        var writer = new FakeNoteWriter("recent") { Pending = new TaskCompletionSource() };
        var started = new List<Project>();
        var note = new QuickNoteViewModel([Recent], writer, (project, _) => { started.Add(project); return Task.CompletedTask; }) { Note = "call finance" };
        var saved = 0;
        note.Saved += (_, _) => saved++;

        var save = note.SaveCommand.ExecuteAsync(null);

        // While the write is in flight the box is locked, so nothing typed can diverge from what the writer holds.
        Assert.True(note.IsSaving);
        Assert.False(note.CanEdit);
        writer.Pending.SetResult();
        await save;

        Assert.True(note.CanEdit);
        Assert.Equal([(Recent, "call finance")], writer.Appended);
        Assert.Empty(started);
        Assert.Equal(1, saved);
        // What landed is no longer a draft, so the next window opens empty rather than offering it a second time.
        Assert.Equal(string.Empty, note.Note);
    }

    // The one hard rule: a failed write keeps the note in the box, says why, starts nothing and survives Escape to the next window.
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

    // A start that fails after the write landed is the one variant a note must survive without being written twice.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveAndStart_StartsOnce_OnlyAfterTheNoteLanded(bool startFails)
    {
        var writer = new FakeNoteWriter("recent");
        var startedAfterWrite = new List<bool>();
        var note = new QuickNoteViewModel([Recent], writer, (_, text) =>
        {
            startedAfterWrite.Add(writer.Appended.Count == 1 && text == "call finance");
            return startFails ? Task.FromException(new InvalidOperationException("no profile")) : Task.CompletedTask;
        }) { Note = "call finance" };
        var saved = 0;
        note.Saved += (_, _) => saved++;

        await note.SaveAndStartCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, note.Note);
        Assert.Equal(startFails, note.Message.Contains("no profile"));
        Assert.Equal(startFails ? 0 : 1, saved);

        // A second click on what already landed writes nothing and starts nothing.
        await note.SaveAndStartCommand.ExecuteAsync(null);
        Assert.Equal([true], startedAfterWrite);
        Assert.Single(writer.Appended);
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

        // Set to hold the write open until the test completes it; left null, the write lands at once.
        public TaskCompletionSource? Pending { get; init; }

        public List<(Project Project, string Note)> Appended { get; } = [];

        public bool CanAppend(Project project) => writableProjectIds.Contains(project.Id);

        public async Task<ProjectMemoryAppendResult> AppendAsync(Project project, string note, CancellationToken cancellationToken)
        {
            Appended.Add((project, note));
            await (Pending?.Task ?? Task.CompletedTask);
            return Result;
        }
    }
}
