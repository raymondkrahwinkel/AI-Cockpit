using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Xunit.Abstractions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The band the "Thinking…" indicator and the "Starting the session" banner live in, above the composer (AC-424).
/// A report said the indicator painted over what sits below it. It does not, and these pin why it cannot: each row is
/// docked, so it takes a band of its own out of the transcript and hands it back, and the composer — the fill child of
/// a bottom-docked panel — never moves. What the report actually showed was the transcript's bottom row cut mid-glyph
/// at the scroll edge with nothing drawn between it and the indicator; the hairline this fixture also guards is what
/// separates the two.
/// </summary>
[Collection("avalonia")]
public class SessionInputBandTests(ITestOutputHelper output)
{
    private const string ThinkingLabel = "Thinking…";
    private const string StartingLabel = "Starting the session";

    private sealed record Pane(Window Window, SessionViewModel Session) : IDisposable
    {
        public void Dispose() => Window.Close();
    }

    /// <summary>A pane the size Autopilot gives an embedded step session, hosted the way the host hands one over.</summary>
    private static Pane _Pane(Action<SessionViewModel>? arrange = null)
    {
        var session = new SessionViewModel();
        session.QueuedMessages.Clear();
        session.PendingAttachments.Clear();
        arrange?.Invoke(session);

        var window = new Window { Width = 620, Height = 480, Content = new ContentControl { Content = session } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return new Pane(window, session);
    }

    private static Rect _Absolute(Visual control, Visual root)
    {
        var origin = control.TranslatePoint(default, root) ?? default;
        return new Rect(origin, control.Bounds.Size);
    }

    /// <summary>The row's own Border — the innermost one carrying the label, never an ancestor that merely contains it.</summary>
    private static Border _Row(Visual root, string label) => root.GetVisualDescendants()
        .OfType<Border>()
        .Last(border => border.Child is StackPanel panel
            && panel.Children.OfType<TextBlock>()
                .Any(text => text.Text?.StartsWith(label, StringComparison.Ordinal) == true));

    private static TextBox _Composer(Visual root) =>
        root.GetVisualDescendants().OfType<TextBox>().First(box => box.Name == "InputBox");

    private static ScrollViewer _Transcript(Visual root) =>
        root.GetVisualDescendants().OfType<ScrollViewer>().First(scroll => scroll.Name == "TranscriptScroll");

    /// <summary>The Border wrapping everything below the transcript: the chrome that carries the separating hairline.</summary>
    private static Border _InputBand(Visual root) => root.GetVisualDescendants()
        .OfType<Border>()
        .First(border => border.Child is DockPanel panel
            && panel.GetVisualDescendants().OfType<TextBox>().Any(box => box.Name == "InputBox"));

    private static void _Settle(Pane pane)
    {
        Dispatcher.UIThread.RunJobs();
        pane.Window.UpdateLayout();
    }

    [Fact]
    public void TheThinkingRow_TakesItsBandFromTheTranscript_AndTheComposerStaysPut() => HeadlessAvalonia.Run(() =>
    {
        using var pane = _Pane();

        var restingComposer = _Absolute(_Composer(pane.Window), pane.Window);
        var restingTranscript = _Absolute(_Transcript(pane.Window), pane.Window);

        pane.Session.IsAwaitingResponse = true;
        _Settle(pane);

        var row = _Absolute(_Row(pane.Window, ThinkingLabel), pane.Window);
        var busyComposer = _Absolute(_Composer(pane.Window), pane.Window);
        var busyTranscript = _Absolute(_Transcript(pane.Window), pane.Window);

        // The composer is the fill child of a bottom-docked panel, so its box is fixed and the row cannot push into it.
        Assert.Equal(restingComposer, busyComposer);
        Assert.False(row.Intersects(busyComposer), $"the row at {row} reached the composer at {busyComposer}");
        Assert.False(row.Intersects(busyTranscript), $"the row at {row} reached the transcript at {busyTranscript}");

        // The band comes out of the transcript, and the row sits below what the transcript still owns.
        Assert.True(busyTranscript.Height < restingTranscript.Height,
            $"the transcript kept {busyTranscript.Height} of {restingTranscript.Height} while the row was up");
        Assert.True(row.Y >= busyTranscript.Bottom, $"the row at {row.Y} started above the transcript's edge at {busyTranscript.Bottom}");

        pane.Session.IsAwaitingResponse = false;
        _Settle(pane);

        // And it hands the band straight back.
        Assert.Equal(restingTranscript, _Absolute(_Transcript(pane.Window), pane.Window));
        Assert.Equal(restingComposer, _Absolute(_Composer(pane.Window), pane.Window));
    });

    [Fact]
    public void RepeatedTogglingWithinOneTurn_NeverPutsTheRowOverTheComposer() => HeadlessAvalonia.Run(() =>
    {
        using var pane = _Pane();

        // A turn with several tool calls flips this many times over: a tool result re-arms the indicator and the next
        // tool call douses it, so one turn is a run of toggles rather than a single appearance.
        var seen = new List<Rect>();
        for (var toggle = 0; toggle < 12; toggle++)
        {
            pane.Session.IsAwaitingResponse = toggle % 2 == 0;
            _Settle(pane);

            var composer = _Absolute(_Composer(pane.Window), pane.Window);
            var transcript = _Absolute(_Transcript(pane.Window), pane.Window);
            var row = _Absolute(_Row(pane.Window, ThinkingLabel), pane.Window);

            if (pane.Session.IsAwaitingResponse)
            {
                Assert.False(row.Intersects(composer), $"toggle {toggle}: the row at {row} reached the composer at {composer}");
                Assert.False(row.Intersects(transcript), $"toggle {toggle}: the row at {row} reached the transcript at {transcript}");
                seen.Add(row);
            }
        }

        // Every appearance lands in the same place: the band does not creep as the turn goes on.
        Assert.Single(seen.Distinct());
        output.WriteLine($"the row settled at {seen[0]} on all {seen.Count} appearances");
    });

    [Fact]
    public void TheStartingBanner_TakesItsOwnBandToo_EvenWhileTheRowIsUp() => HeadlessAvalonia.Run(() =>
    {
        // Autopilot starts a step session and injects its brief straight away, so unlike a pane someone types into,
        // both are up at once and the banner then goes while the turn is still in flight.
        using var pane = _Pane(session =>
        {
            session.IsStarting = true;
            session.IsAwaitingResponse = true;
        });

        var composer = _Absolute(_Composer(pane.Window), pane.Window);
        var banner = _Absolute(_Row(pane.Window, StartingLabel), pane.Window);
        var row = _Absolute(_Row(pane.Window, ThinkingLabel), pane.Window);

        Assert.False(banner.Intersects(row), $"the banner at {banner} reached the row at {row}");
        Assert.False(banner.Intersects(composer), $"the banner at {banner} reached the composer at {composer}");
        Assert.False(row.Intersects(composer), $"the row at {row} reached the composer at {composer}");

        // Declaration order is what puts the banner above the row; both are docked, so neither can land on the other.
        Assert.True(banner.Bottom <= row.Y, $"the banner ended at {banner.Bottom}, below the row's top at {row.Y}");

        var startingTranscript = _Absolute(_Transcript(pane.Window), pane.Window);

        pane.Session.IsStarting = false;
        _Settle(pane);

        // The banner's band goes back to the transcript, not to the rows below it: everything under the banner is
        // anchored to the bottom-docked panel's fixed edge, so the indicator does not shift under a vanishing banner.
        var afterStart = _Absolute(_Row(pane.Window, ThinkingLabel), pane.Window);
        Assert.Equal(row, afterStart);
        Assert.Equal(composer, _Absolute(_Composer(pane.Window), pane.Window));
        Assert.True(_Absolute(_Transcript(pane.Window), pane.Window).Height > startingTranscript.Height,
            "the transcript should get the banner's band back");
    });

    [Fact]
    public void TheComposerZone_IsSeparatedFromTheTranscript_ByALine() => HeadlessAvalonia.Run(() =>
    {
        using var pane = _Pane(session => session.IsAwaitingResponse = true);

        var band = _InputBand(pane.Window);
        var bandRect = _Absolute(band, pane.Window);
        var transcript = _Absolute(_Transcript(pane.Window), pane.Window);
        var row = _Absolute(_Row(pane.Window, ThinkingLabel), pane.Window);

        // Without this the transcript's bottom row is cut mid-glyph against the same background the indicator sits on,
        // and the cut reads as a bar colliding with the row rather than as content scrolled out of view.
        Assert.True(band.BorderThickness.Top > 0, "the composer zone has no line separating it from the transcript");
        Assert.Equal(
            (Application.Current?.FindResource("CockpitHairlineBrush") as ISolidColorBrush)?.Color,
            (band.BorderBrush as ISolidColorBrush)?.Color);

        // The line belongs between the two: below everything the transcript owns, above the indicator.
        Assert.True(bandRect.Y >= transcript.Bottom, $"the line at {bandRect.Y} cut into the transcript ending at {transcript.Bottom}");
        Assert.True(bandRect.Y < row.Y, $"the line at {bandRect.Y} sat below the row at {row.Y}");
    });

    [Fact]
    public void AViewReparentedOnEveryRender_KeepsTheRowInItsOwnBand() => HeadlessAvalonia.Run(() =>
    {
        // Autopilot's run surface rebuilds around a persistent session view and reparents it each render
        // (AutopilotPlanWorkspaceBody._DetachFromParent), which a normal pane never does to a live view.
        var session = new SessionViewModel();
        session.QueuedMessages.Clear();
        session.PendingAttachments.Clear();
        session.IsAwaitingResponse = true;

        var view = new ContentControl { Content = session };
        var window = new Window { Width = 620, Height = 480 };

        try
        {
            for (var render = 0; render < 6; render++)
            {
                if (view.Parent is Decorator holder)
                {
                    holder.Child = null;
                }

                window.Content = new DockPanel
                {
                    LastChildFill = true,
                    Children =
                    {
                        new Border { [DockPanel.DockProperty] = Dock.Top, Child = new TextBlock { Text = "step" } },
                        new Border { Child = view },
                    },
                };

                if (render == 0)
                {
                    window.Show();
                }

                session.IsAwaitingResponse = render % 2 == 0;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                var row = _Absolute(_Row(window, ThinkingLabel), window);
                var composer = _Absolute(_Composer(window), window);
                Assert.False(row.Intersects(composer), $"render {render}: the row at {row} reached the composer at {composer}");
            }
        }
        finally
        {
            window.Close();
        }
    });
}
