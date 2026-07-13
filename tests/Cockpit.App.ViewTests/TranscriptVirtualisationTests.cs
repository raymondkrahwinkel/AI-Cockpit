using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using FluentAssertions;

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
        Dispatcher.UIThread.RunJobs();

        var rows = window.GetVisualDescendants()
            .OfType<Border>()
            .Count(border => border.Classes.Contains("transcriptRow"));

        window.Close();

        // A 600px-tall window cannot show four hundred rows. Anything close to four hundred means the panel is
        // building the whole history again.
        rows.Should().BeGreaterThan(0, "the rows on screen must actually be there");
        rows.Should().BeLessThan(100, "only what fits on screen (plus a little) should exist as controls");
    });
}
