using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Sessions;
using Microsoft.Extensions.Logging;

namespace Cockpit.Core.Tests.Sessions;

/// <summary>
/// What a scheduled resume leaves behind (AC-368). The feature ran for its whole life without sending anything and
/// nobody could tell, because it wrote nothing down anywhere: no line when one was scheduled, none when one fired,
/// none when one could not be delivered. A resume that does not arrive has to be explainable afterwards.
/// </summary>
/// <remarks>
/// Asserted with xunit's own Assert: the fluent library its neighbours use is on its way out (AC-372).
/// </remarks>
public class ScheduledResumeTraceTests
{
    private static ScheduledResume Resume(string paneId, DateTimeOffset dueAt, string prompt = "continue") =>
        new(paneId, dueAt, prompt, Reason: "Week is 95% used");

    private static (ScheduledResumeCoordinator Coordinator, CapturingLogger<ScheduledResumeCoordinator> Logger)
        Build(InMemoryScheduledResumeStore store, Func<string, SessionPanelViewModel?>? resolve = null)
    {
        var logger = new CapturingLogger<ScheduledResumeCoordinator>();
        var coordinator = new ScheduledResumeCoordinator(store, toast: null, logger) { ResolveSession = resolve };

        return (coordinator, logger);
    }

    [Fact]
    public async Task SchedulingOne_SaysWhichSessionAndWhen()
    {
        var (coordinator, logger) = Build(new InMemoryScheduledResumeStore());

        await coordinator.ScheduleAsync(Resume("pane-1", DateTimeOffset.Now.AddHours(1)));

        Assert.Contains(logger.Messages, message => message.Contains("pane-1") && message.Contains("scheduled"));
    }

    [Fact]
    public async Task SendingOne_SaysSo()
    {
        var session = new TestSessionPanel();
        var (coordinator, logger) = Build(new InMemoryScheduledResumeStore(), _ => session);

        await coordinator.ScheduleAsync(Resume("pane-1", DateTimeOffset.Now.AddMinutes(-1)));
        await coordinator.RunDueAsync(DateTimeOffset.Now);

        Assert.Contains(logger.Messages, message => message.Contains("pane-1") && message.Contains("was sent"));
    }

    [Fact]
    public async Task OneThatCouldNotBeDelivered_IsWarnedAbout_NotOnlyToasted()
    {
        // A toast is gone in seconds and only if somebody was looking. This is the half that is still there tomorrow.
        var (coordinator, logger) = Build(new InMemoryScheduledResumeStore(), _ => null);

        await coordinator.ScheduleAsync(Resume("pane-gone", DateTimeOffset.Now.AddMinutes(-1)));
        await coordinator.RunDueAsync(DateTimeOffset.Now);

        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains(logger.Messages, message => message.Contains("pane-gone") && message.Contains("could not be delivered"));
    }

    [Fact]
    public async Task OneThatLapsedWhileTheCockpitWasClosed_IsWarnedAbout()
    {
        var store = new InMemoryScheduledResumeStore { Saved = [Resume("pane-1", DateTimeOffset.Now.AddHours(-4))] };
        var (coordinator, logger) = Build(store);

        await coordinator.LoadAsync();

        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains(logger.Messages, message => message.Contains("pane-1") && message.Contains("lapsed"));
    }

    [Fact]
    public async Task LoadingTwice_HoldsEachResumeOnce_NotTwice()
    {
        // "Take up what was scheduled" replaces what is held; it does not add to it. Appending would mean the same
        // resume sits in the list twice and is delivered twice — the one failure worse than not delivering it.
        var store = new InMemoryScheduledResumeStore { Saved = [Resume("pane-1", DateTimeOffset.Now.AddHours(2))] };
        var (coordinator, _) = Build(store);

        await coordinator.LoadAsync();
        await coordinator.LoadAsync();

        Assert.Single(coordinator.Pending);
    }

    [Fact]
    public async Task CancellingOne_SaysSo()
    {
        // The open end on AC-368 was a store that turned out empty with no explanation. Every route that empties it
        // now leaves a line, so "it was cancelled" and "it was never written down" stop looking the same.
        var (coordinator, logger) = Build(new InMemoryScheduledResumeStore());
        await coordinator.ScheduleAsync(Resume("pane-1", DateTimeOffset.Now.AddHours(1)));

        await coordinator.CancelAsync("pane-1");

        Assert.Contains(logger.Messages, message => message.Contains("pane-1") && message.Contains("cancelled"));
    }
}
