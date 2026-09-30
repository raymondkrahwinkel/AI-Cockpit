using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Sessions;
using Cockpit.Core.Toasts;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.Backend.Tests.Sessions;

/// <summary>
/// The machinery under a scheduled resume (AC-234): it remembers what is waiting, sends it when its moment comes,
/// and says so when it could not. What it must never do is send a prompt somewhere it does not belong — "continue"
/// with no history behind it is meaningless, and worse than nothing because it looks like it worked.
/// </summary>
public class ScheduledResumeCoordinatorTests
{
    private sealed class InMemoryStore : IScheduledResumeStore
    {
        public List<ScheduledResume> Saved { get; set; } = [];

        public Task<IReadOnlyList<ScheduledResume>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ScheduledResume>>(Saved);

        public Task SaveAsync(IReadOnlyList<ScheduledResume> resumes, CancellationToken cancellationToken = default)
        {
            Saved = [.. resumes];
            return Task.CompletedTask;
        }
    }

    /// <summary>Records every toast shown, so a test can tell the "reopened and sent" report apart from the ordinary "could not be delivered" one.</summary>
    private sealed class RecordingToast : IToastService
    {
        public List<(string Message, ToastSeverity Severity)> Shown { get; } = [];

        public void Show(string message, ToastSeverity severity, string? actionLabel = null, Action? onAction = null) =>
            Shown.Add((message, severity));
    }

    private static ScheduledResume Resume(string paneId, DateTimeOffset dueAt, string prompt = "continue") =>
        new(paneId, dueAt, prompt, Reason: "Week is 95% used");

    [Fact]
    public async Task Scheduling_PersistsImmediately_SoItSurvivesTheAppClosing()
    {
        // The window a resume exists to cover is exactly the one where the cockpit may not be running.
        var store = new InMemoryStore();
        var coordinator = new ScheduledResumeCoordinator(store);

        await coordinator.ScheduleAsync(Resume("pane-1", DateTimeOffset.Now.AddHours(1)));

        Assert.Equal("pane-1", Assert.Single(store.Saved).PaneId);
    }

}
