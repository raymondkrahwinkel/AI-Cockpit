using Cockpit.Infrastructure.Plugins;
using Cockpit.App.Composition;
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
    public async Task Save_WritesTheNote_StartsNothing_AndLeavesNoDraftBehind()
    {
        var writer = new FakeNoteWriter("recent") { Pending = new TaskCompletionSource() };
        var started = new List<Project>();
        var note = new QuickNoteViewModel([Recent], writer.AppendAsync, (project, _) => { started.Add(project); return Task.CompletedTask; }) { Note = "call finance" };
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

    // A coordinator over a cockpit that holds exactly `projects`.
    private static async Task<QuickNoteCoordinator> _CoordinatorAsync(FakeNoteWriter writer, params Project[] projects)
    {
        var cockpit = TestCockpit.NewViewModel();
        foreach (var project in projects)
        {
            await cockpit.Projects.AddNewProjectAsync(project);
        }

        return new QuickNoteCoordinator(TestGlobalHotkeys.Coordinator(new FakeGlobalHotkeyService()), cockpit, new ProjectMemoryNotes(writer));
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
