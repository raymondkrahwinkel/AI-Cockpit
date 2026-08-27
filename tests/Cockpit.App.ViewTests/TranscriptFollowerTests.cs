using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1121. The transcript follow used to run from <c>ScrollChanged</c>, which Avalonia raises from
/// <c>LayoutUpdated</c> — so each follow queued a layout pass inside the pass that raised it, and AC-1178 caught
/// that chain in three live stacks of a frozen dev app. These pin the three properties the rewrite rests on.
/// </summary>
[Collection("avalonia")]
public sealed class TranscriptFollowerTests
{
    private static SessionViewModel _Session(int rows)
    {
        var session = new SessionViewModel();
        session.Transcript.Clear();
        for (var index = 0; index < rows; index++)
        {
            session.Transcript.Add(new TranscriptEntryViewModel(
                TranscriptEntryKind.AssistantText,
                $"row {index} of a reply long enough to wrap more than once in this viewport."));
        }

        return session;
    }

    private static ScrollViewer _Transcript(Visual root) =>
        root.GetVisualDescendants().OfType<ScrollViewer>().First(scroll => scroll.Name == "TranscriptScroll");

    private static void _Settle(Window window)
    {
        // A step runs at Loaded and the arrange it drives comes after, so one round of each is not enough to
        // reach a fixpoint — the follow converges over frames now, by design, instead of nesting inside one.
        for (var round = 0; round < 8; round++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    /// <summary>
    /// The structural claim, measured where it bites: no viewport move happens while a <c>ScrollChanged</c> is
    /// being handled. The first handler runs before the view's own (it is subscribed before the attach that wires
    /// the view's) and the last runs after it, so between them sits exactly the view's handler — which is where
    /// the follow used to move the viewport, and where the nested layout pass came from.
    /// </summary>
    [Fact]
    public void AScrollChange_IsNeverAnsweredWithAViewportMove() => HeadlessAvalonia.Run(() =>
    {
        var session = _Session(40);
        var view = new SessionView { DataContext = session };

        // Before Show, so this lands ahead of the handler OnAttachedToVisualTree adds.
        view.TranscriptItems.ApplyTemplate();
        var scroll = view.TranscriptItems.GetVisualChildren().OfType<ScrollViewer>().First();

        var offsetBefore = double.NaN;
        var moved = 0;
        var seen = 0;
        scroll.ScrollChanged += (_, _) => offsetBefore = scroll.Offset.Y;

        var window = new Window { Content = view, Width = 820, Height = 560 };
        window.Show();
        _Settle(window);

        scroll.ScrollChanged += (_, _) =>
        {
            seen++;
            if (!TranscriptScrollAnchor.IsSettled(offsetBefore, scroll.Offset.Y))
            {
                moved++;
            }
        };

        for (var index = 0; index < 20; index++)
        {
            session.Transcript.Add(new TranscriptEntryViewModel(
                TranscriptEntryKind.AssistantText, $"streamed row {index} that wraps across a couple of lines."));
            _Settle(window);
        }

        Assert.True(seen > 0, "the fixture produced no scroll changes at all, so it asserts nothing");
        Assert.Equal(0, moved);

        window.Close();
    });

    /// <summary>
    /// AC-1178's gate. From five sessions on, <c>SessionTilePanel</c> arranges a rail tile outside its own box and
    /// clips it — the tile stays <c>IsVisible</c> and stays laid out — and the follow of that tile is what reached
    /// Avalonia's cut-off of 153 layout rounds in a frame. A clipped-away pane does not follow.
    /// </summary>
    [Theory]
    [InlineData(0.0, true)]
    [InlineData(4000.0, false)]
    public void APaneClippedOutOfItsParent_DoesNotFollow(double top, bool expectFollow) => HeadlessAvalonia.Run(() =>
    {
        var session = _Session(40);
        var view = new SessionView { DataContext = session, Width = 700, Height = 460 };
        var canvas = new Canvas { ClipToBounds = true };
        Canvas.SetTop(view, top);
        canvas.Children.Add(view);

        var window = new Window { Content = canvas, Width = 760, Height = 500 };
        window.Show();
        _Settle(window);

        var scroll = _Transcript(window);
        scroll.Offset = scroll.Offset.WithY(0);
        _Settle(window);

        session.Transcript.Add(new TranscriptEntryViewModel(
            TranscriptEntryKind.AssistantText, "the row that would be followed to the tail."));
        _Settle(window);

        // The visible case is the negative control: it is the same binary, the same rows and the same pump, so a
        // gate that simply never lets anything follow cannot pass this theory.
        Assert.Equal(expectFollow, scroll.Offset.Y > 0);

        window.Close();
    });

    /// <summary>
    /// AC-1130. Resolving the scroll owner ends on a template lookup, and it used to be the first line of
    /// <c>OnDetachedFromVisualTree</c> with <c>.First()</c> under it — so a template without one threw, and every
    /// unsubscribe after it was skipped, including the one from a process-lifetime singleton.
    /// </summary>
    [Fact]
    public void DetachingAPaneWhoseTemplateHasNoScrollOwner_DoesNotThrow() => HeadlessAvalonia.Run(() =>
    {
        var view = new SessionView { DataContext = _Session(5) };
        view.TranscriptItems.Template = new FuncControlTemplate<ItemsControl>((_, _) => new Border());

        var window = new Window { Content = view, Width = 700, Height = 460 };
        window.Show();
        window.UpdateLayout();

        // The detach itself is the assertion: before AC-1130 this threw on its first line.
        window.Content = null;
        window.UpdateLayout();

        window.Close();
    });
}
