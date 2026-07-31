using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-532's composer activity band needs its elapsed time to visibly count up ("running 0:12"), which the view
/// model cannot do on its own — the derived state (which tool, since when) has to stay dispatcher-free so
/// <c>Cockpit.Core.Tests</c> can drive <c>SessionViewModel.Apply</c> directly with no Avalonia platform up. So
/// <see cref="Cockpit.App.Views.SessionView"/>'s code-behind owns a one-second <see cref="DispatcherTimer"/> that
/// just re-raises <c>ActiveToolActivityAgeText</c>'s change notification. That wiring had zero coverage; the same
/// class of bug <see cref="ScheduledResumeTimerTests"/> exists for (a timer built off the UI thread that never
/// ticks, or one a detach fails to stop so it keeps ticking a control nobody can see any more) applies here too.
/// </summary>
[Collection("avalonia")]
public class SessionActivityTickerTests
{
    // The ticker's own interval is a hardcoded one second (SessionView.axaml.cs); these windows are long enough to
    // catch at least one real tick without being so tight that ordinary dispatcher jitter makes the test flaky.
    private static readonly TimeSpan OneTickOrSo = TimeSpan.FromMilliseconds(1300);
    private static readonly TimeSpan AFewTicks = TimeSpan.FromMilliseconds(2600);

    [Fact]
    public Task Ticker_StartsOnAttach_StopsOnDetach_AndNeverDoublesOnReattach() => HeadlessAvalonia.RunAsync(async () =>
    {
        var session = new SessionViewModel();
        session.QueuedMessages.Clear();
        session.PendingAttachments.Clear();

        var view = new ContentControl { Content = session };
        var window = new Window { Width = 620, Height = 480 };

        try
        {
            // Attach: the ticker must actually be running (AC-532's own AC8 wants this covered, not assumed).
            var ticksWhileAttached = await _RunFor(session, AFewTicks, () =>
            {
                window.Content = view;
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
            });
            Assert.True(ticksWhileAttached >= 1, "the ticker never fired while the view was attached and visible");

            // Detach: a timer left running would keep ticking a view nobody can see any more.
            var ticksAfterDetach = await _RunFor(session, OneTickOrSo, () =>
            {
                window.Content = null;
                Dispatcher.UIThread.RunJobs();
            });
            Assert.Equal(0, ticksAfterDetach);

            // Reattach: a Stop() that failed to run (or a timer field never cleared) would leave the old one
            // ticking alongside a fresh one, doubling the rate — one timer over ~2.6s at a 1s interval ticks
            // twice; two would tick roughly four times.
            var ticksAfterReattach = await _RunFor(session, AFewTicks, () =>
            {
                window.Content = view;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
            });
            Assert.InRange(ticksAfterReattach, 1, 3);
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>
    /// AC-531: the same ticker also re-raises AgeText on every outstanding background-task row, not just the
    /// composer's own tool-activity band — verified directly on a task's own <see cref="BackgroundTaskViewModel"/>
    /// rather than on the session, since that is the object whose PropertyChanged actually fires.
    /// </summary>
    [Fact]
    public Task Ticker_AlsoReRaisesAgeTextOnOutstandingBackgroundTaskRows() => HeadlessAvalonia.RunAsync(async () =>
    {
        var session = new SessionViewModel();
        session.QueuedMessages.Clear();
        session.PendingAttachments.Clear();
        session.Apply(new BackgroundTasksChanged
        {
            SessionId = "s1",
            Tasks = [new BackgroundTask("a1", BackgroundTaskKind.SubAgent, "Agent 1")],
        });
        var row = session.BackgroundSubAgents[0];

        var view = new ContentControl { Content = session };
        var window = new Window { Width = 620, Height = 480 };

        try
        {
            var ticks = 0;
            void OnRowChanged(object? sender, PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(BackgroundTaskViewModel.AgeText))
                {
                    ticks++;
                }
            }

            row.PropertyChanged += OnRowChanged;
            try
            {
                window.Content = view;
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                await Task.Delay(AFewTicks);
            }
            finally
            {
                row.PropertyChanged -= OnRowChanged;
            }

            Assert.True(ticks >= 1, "the ticker never re-raised AgeText on a background task row while the view was attached");
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>Runs <paramref name="arrange"/> (which may synchronously flip the view's attachment state), then counts ticks over <paramref name="window"/> of real time — the dispatcher keeps pumping while this awaits, which is what lets the timer actually fire.</summary>
    private static async Task<int> _RunFor(SessionViewModel session, TimeSpan window, Action arrange)
    {
        var ticks = 0;
        void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SessionViewModel.ActiveToolActivityAgeText))
            {
                ticks++;
            }
        }

        session.PropertyChanged += OnPropertyChanged;
        try
        {
            arrange();
            await Task.Delay(window);
        }
        finally
        {
            session.PropertyChanged -= OnPropertyChanged;
        }

        return ticks;
    }
}
