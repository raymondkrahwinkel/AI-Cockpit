using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.App.ViewTests;

// AC-1658: a save that lands closes the quick-note window; one that fails leaves it open with the note and the reason.
[Collection("avalonia")]
public sealed class Ac1658QuickNoteClosesTests
{
    [Fact]
    public void ASavedNote_ClosesTheWindow_AndAFailedOne_KeepsItOpenWithTheNote() => HeadlessAvalonia.Run(() =>
    {
        var (saved, savedNote) = _Open(ProjectMemoryAppendOutcome.Success);
        var (failed, failedNote) = _Open(ProjectMemoryAppendOutcome.Failed);
        try
        {
            savedNote.SaveCommand.Execute(null);
            failedNote.SaveCommand.Execute(null);

            Assert.False(saved.IsVisible);
            Assert.True(failed.IsVisible);
            Assert.Equal("a thought", failedNote.Note);
            Assert.Contains("disk full", failedNote.Message);
        }
        finally
        {
            saved.Close();
            failed.Close();
        }
    });

    private static (QuickNoteWindow Window, QuickNoteViewModel Note) _Open(ProjectMemoryAppendOutcome outcome)
    {
        var note = new QuickNoteViewModel(
            [new Project("p1", "Cockpit")],
            (_, _, _) => Task.FromResult(new ProjectMemoryAppendResult(outcome, outcome == ProjectMemoryAppendOutcome.Success ? null : "disk full")),
            start: null)
        { Note = "a thought" };
        var window = new QuickNoteWindow { DataContext = note };
        window.Show();
        return (window, note);
    }
}
