using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1111: the follow used to ask <c>ScrollIntoView</c> for the newest row, which runs a whole nested layout pass
/// over the window there and then — and while streaming it ran once per arriving row, because a row that has just
/// been added is never realised yet. Measured at 6.8MB and tens of milliseconds each, that is the freeze and the
/// heap growth in one: the UI thread stops keeping up and the garbage it makes never gets collected.
/// </summary>
[Collection("avalonia")]
public sealed class TranscriptFollowCostTests
{
    // Generous enough that fonts, machines and row heights cannot decide the outcome: the regression it guards
    // against allocated ~6.8MB per row against the ~1.2MB a plain frame costs.
    private const double BudgetPerRowMb = 3.0;

    private const int StreamedRows = 200;

    // A real frame rather than UpdateLayout: the freeze's stack starts in MediaContext.Render, and it is that pass
    // the follow re-entered. Driving layout by hand would measure a shape the app never runs.
    private static void _Frame(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Streams rows into a pane parked at the newest one and holds the follow to a budget per row, while requiring
    /// that it still lands on the tail — a follow that stopped working would otherwise pass this on cost alone.
    /// </summary>
    [Fact]
    public void StreamingRows_FollowsTheNewestOne_WithoutALayoutPassPerRow() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();
        session.Transcript.Clear();
        for (var index = 0; index < 60; index++)
        {
            session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, $"row {index}"));
        }

        var view = new SessionView { DataContext = session };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        _Frame(window);

        view.TranscriptScroll.ScrollToEnd();
        _Frame(window);

        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        for (var index = 0; index < StreamedRows; index++)
        {
            session.Transcript.Add(new TranscriptEntryViewModel(
                TranscriptEntryKind.AssistantText, $"streamed {index} of a reply that wraps over a couple of lines."));
            _Frame(window);
        }

        var perRowMb = (GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore) / 1024.0 / 1024.0 / StreamedRows;

        var newest = view.TranscriptItems.ContainerFromIndex(view.TranscriptItems.ItemCount - 1);
        var viewport = view.TranscriptScroll.Viewport.Height;
        var tail = newest?.TranslatePoint(new Point(0, newest.Bounds.Height), view.TranscriptScroll)?.Y;

        window.Close();

        Assert.True(
            tail is not null && tail <= viewport + 1.0,
            $"the newest row's tail sits at {tail?.ToString("F0") ?? "an unrealised row"} in a {viewport:F0}px "
            + "viewport: the transcript stopped following the tail");

        Assert.True(
            perRowMb < BudgetPerRowMb,
            $"{perRowMb:F1}MB allocated per streamed row against a {BudgetPerRowMb:F1}MB budget: the follow is "
            + "forcing a synchronous layout pass per arriving row again (AC-1111)");
    });
}
