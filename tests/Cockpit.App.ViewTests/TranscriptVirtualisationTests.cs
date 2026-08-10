using Avalonia.Controls;
using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The transcript builds only the rows on screen. Without this, every row a session ever produced stays alive as a
/// full control tree — hundreds of megabytes of user interface nobody is looking at, in the sessions that run longest
/// and matter most. The saving is invisible from the outside, which is exactly why it needs a test: the day someone
/// swaps the panel back for a plain StackPanel, everything still works and nothing says otherwise.
/// </summary>
[Collection("avalonia")]
public class TranscriptVirtualisationTests
{
    [Fact]
    public void ALongTranscript_BuildsOnlyTheRowsThatFitOnScreen() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();
        for (var index = 0; index < 400; index++)
        {
            session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, $"row {index}"));
        }

        // A real window, because virtualisation needs a real viewport: without a scroll owner the panel has nothing to
        // fit rows into and builds none. Safe here, and only here — a window brings a compositor that the garbage
        // collector tears down on a thread that does not own it, which kills the test host rather than failing a test.
        // That is why this assembly exists.
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = new SessionView { DataContext = session },
        };

        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // The panel's own count of what it has built, rather than a sweep of the visual tree: it is the number the
        // fix is about, and it does not depend on a row's template having rendered a Border yet.
        var view = (SessionView)window.Content!;
        var built = view.TranscriptItems.GetRealizedContainers().Count();

        window.Close();

        // A 600px-tall window cannot show four hundred rows. Anything close to four hundred means the panel is
        // building the whole history again. Note what this does and does not prove: measured either side of AC-686
        // (which moved the scroll owner inside the transcript's own template) this window realises six rows, so the
        // test is a guard against the panel being swapped for one that virtualises nothing — not evidence about the
        // memory the ticket is chasing.
        Assert.True(built > 0, "the rows on screen must actually be there");
        Assert.True(built < 100, $"{built} of 400 rows built: the panel is building history nobody is looking at");
    });
}
