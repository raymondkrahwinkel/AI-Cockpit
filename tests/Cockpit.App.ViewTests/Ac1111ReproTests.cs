using Avalonia;
using Avalonia.Headless;
using Avalonia.Controls;
using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

// AC-1111 measurement scaffold — remove before the PR.
[Collection("avalonia")]
public sealed class Ac1111ReproTests
{
    private static readonly List<SessionView.FollowNewestMeasurement> Hits = [];

    public Ac1111ReproTests() => SessionView.FollowNewestProbe = Hits.Add;

    // A real frame, not just a layout pass: the freeze's stack starts in MediaContext.Render, and it is that pass
    // the follow re-enters. Driving layout by hand would measure a shape the app never runs.
    private static void _Frame(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
        Dispatcher.UIThread.RunJobs();
    }

    private static (Window Window, SessionView View, SessionViewModel Session) _Pane(int rows, int height = 600)
    {
        var session = new SessionViewModel();
        session.Transcript.Clear();
        for (var index = 0; index < rows; index++)
        {
            session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, $"row {index}"));
        }

        var view = new SessionView { DataContext = session };
        var window = new Window { Width = 800, Height = height, Content = view };
        window.Show();
        _Frame(window);
        return (window, view, session);
    }

    // Rows arranged with offsets that no longer match their measured heights: the next row starts above where the
    // one before it ends. Reported in panel coordinates, which is where the panel itself places them.
    private static List<string> _Overlaps(SessionView view)
    {
        var placed = view.TranscriptItems.GetRealizedContainers()
            .Select(container => (Index: view.TranscriptItems.IndexFromContainer(container), container.Bounds))
            .Where(row => row.Index >= 0)
            .OrderBy(row => row.Index)
            .ToList();

        var found = new List<string>();
        for (var step = 1; step < placed.Count; step++)
        {
            var above = placed[step - 1];
            var below = placed[step];
            var overlap = above.Bounds.Y + above.Bounds.Height - below.Bounds.Y;
            if (overlap > 0.5)
            {
                found.Add(
                    $"rows {above.Index}/{below.Index} overlap {overlap:F0}px " +
                    $"({above.Index}: y={above.Bounds.Y:F0} h={above.Bounds.Height:F0}, " +
                    $"{below.Index}: y={below.Bounds.Y:F0} h={below.Bounds.Height:F0})");
            }
        }

        return found;
    }

    private const string Paragraph =
        "This is a long prompt that keeps going and wrapping over several lines so the row it lands in " +
        "grows well past the height of the viewport it has to fit into.\n\n";

    /// <summary>
    /// The cheap repro from the ticket: a row taller than the viewport arriving in a transcript that is growing,
    /// with the pane parked at the newest row. Reports overlapping arranged rows, and the cost of the branch.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARowTallerThanTheViewport_ArrivingWhileParked(bool skipScrollIntoView) => HeadlessAvalonia.Run(() =>
    {
        SessionView.SkipScrollIntoView = skipScrollIntoView;
        var from = Hits.Count;
        try
        {
            var (window, view, session) = _Pane(40);
            view.TranscriptScroll.ScrollToEnd();
            _Frame(window);

            var settled = Hits.Count;
            var overlaps = new List<string>();
            var watch = System.Diagnostics.Stopwatch.StartNew();

            // Three turns: a tall prompt streams in paragraph by paragraph, then ordinary rows land under it.
            for (var turn = 0; turn < 3; turn++)
            {
                var tall = new TranscriptEntryViewModel(TranscriptEntryKind.UserText, $"prompt {turn}\n\n");
                session.Transcript.Add(tall);
                for (var paragraph = 0; paragraph < 12; paragraph++)
                {
                    tall.AppendText(Paragraph);
                    _Frame(window);
                    overlaps.AddRange(_Overlaps(view).Select(line => $"turn {turn} para {paragraph}: {line}"));
                }

                for (var reply = 0; reply < 4; reply++)
                {
                    session.Transcript.Add(new TranscriptEntryViewModel(
                        TranscriptEntryKind.AssistantText, $"reply {turn}.{reply}"));
                    _Frame(window);
                    overlaps.AddRange(_Overlaps(view).Select(line => $"turn {turn} reply {reply}: {line}"));
                }
            }

            watch.Stop();

            var tallest = view.TranscriptItems.GetRealizedContainers().Select(c => c.Bounds.Height).DefaultIfEmpty(0).Max();
            var viewport = view.TranscriptScroll.Viewport.Height;
            var taken = Hits.Skip(settled).ToList();

            // Did the follow actually end up at the bottom?
            var newest = view.TranscriptItems.ContainerFromIndex(view.TranscriptItems.ItemCount - 1);
            var tail = newest?.TranslatePoint(new Point(0, newest.Bounds.Height), view.TranscriptScroll)?.Y;

            window.Close();

            Assert.Fail(
                $"AC-1111 tall-row repro (skipScrollIntoView={skipScrollIntoView}):\n" +
                $"  tallest realised row {tallest:F0}px in a {viewport:F0}px viewport\n" +
                $"  wall={watch.Elapsed.TotalMilliseconds:F0}ms, branch taken {taken.Count}x, " +
                $"allocated={taken.Sum(m => m.AllocatedBytes) / 1024.0 / 1024.0:F1}MB\n" +
                $"  tail of the newest row sits at y={tail?.ToString("F0") ?? "?"} (viewport {viewport:F0})\n" +
                $"  overlapping arrangements: {overlaps.Count}\n" +
                string.Join("\n", overlaps.Take(15).Select(line => "    " + line)));
        }
        finally
        {
            SessionView.SkipScrollIntoView = false;
            _ = from;
        }
    });
}
