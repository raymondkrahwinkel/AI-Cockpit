using Avalonia.Controls;
using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Sessions;

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

        // A 600px-tall window cannot show four hundred rows; anything close to four hundred means the panel is
        // building the whole history again. It realises six either side of AC-686, so this guards the panel against
        // being swapped for one that virtualises nothing — it is not evidence about the memory that ticket chases.
        Assert.True(built > 0, "the rows on screen must actually be there");
        Assert.True(built < 100, $"{built} of 400 rows built: the panel is building history nobody is looking at");
    });

    /// <summary>
    /// Virtualisation counts items, not pixels, so a row hidden at zero height used to cost a full
    /// <see cref="Cockpit.App.Controls.TranscriptRowView"/> for nothing. Measured before AC-800: 71 realised rows at Focus
    /// against Developer's 15, which is what "scrolling is jerky at Focus" was.
    /// </summary>
    [Fact]
    public void AtFocus_TheFoldedStepsCostNothingToScrollPast() => HeadlessAvalonia.Run(() =>
    {
        static int _Realised(ReadingLevel level)
        {
            var session = new SessionViewModel();
            session.Transcript.Clear();
            session.ReadingLevel = level;

            // One line of prose per run of nine folded tool calls: the shape of an agent that works in bursts, and
            // the shape that made the panel build ten rows for every one the operator can see.
            for (var index = 0; index < 600; index++)
            {
                if (index % 10 == 0)
                {
                    session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, $"row {index}"));
                    continue;
                }

                session.Apply(new ToolUseRequested
                {
                    SessionId = "S1", ToolUseId = $"tool-{index}", ToolName = "Bash", InputJson = "{}",
                });
            }

            var window = new Window
            {
                Width = 1000,
                Height = 700,
                Content = new SessionView { DataContext = session },
            };

            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var realised = ((SessionView)window.Content!).TranscriptItems.GetRealizedContainers().Count();
            window.Close();
            return realised;
        }

        var developer = _Realised(ReadingLevel.Developer);
        var focus = _Realised(ReadingLevel.Focus);

        // Focus shows fewer rows than Developer in the same viewport, so it can never legitimately need more of
        // them built. Twice the slack is for the taller rows Focus's anchors carry, not for a folded run's worth.
        Assert.True(
            focus <= developer * 2,
            $"Focus built {focus} rows where Developer built {developer}: the panel is building the folded steps again");
    });
}
