using System.Text.Json;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Autopilot.Tests;

// AC-346 review, MEDIUM 6: a second click on an epic (or issue) while its run is already in flight must not start
// a duplicate. Exercises `_HasRunInFlight` against real `AutopilotPlanController`/`AutopilotRunQueue`. The third
// waiting place, `AutopilotRunManager.Active`, needs scaffolding AutopilotRunCoordinatorTests already builds, so it is covered by review instead.
public class AutopilotDuplicateRunGuardTests
{
    private sealed class FakeStorage : IPluginStorage
    {
        private readonly Dictionary<string, string> _data = new(StringComparer.Ordinal);
        public T? Get<T>(string key) => _data.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : default;
        public void Set<T>(string key, T value) => _data[key] = JsonSerializer.Serialize(value);
        public void SetSecret(string key, string value) => Set(key, value);
        public string? GetSecret(string key) => Get<string>(key);
    }

    private static AutopilotPlan Plan(string tracker, string issueId) =>
        AutopilotPlan.Empty(new AutopilotPlanSource(tracker, issueId, "title"), "goal") with { Name = "goal" };

    private static (AutopilotPlanController PlanController, AutopilotRunQueue Queue, AutopilotRunManager Manager) Fresh()
    {
        var storage = new FakeStorage();
        var queue = new AutopilotRunQueue(storage);
        var manager = new AutopilotRunManager(queue, new AutopilotSettings(storage));
        return (new AutopilotPlanController(), queue, manager);
    }

    [Fact]
    public void HasRunInFlight_ForAFreshController_IsFalse()
    {
        var (planController, queue, manager) = Fresh();

        Assert.False(AutopilotPlugin._HasRunInFlight(planController, queue, manager, "youtrack", "AC-1"));
    }

    [Theory]
    // The window a double-click on the tracker button most often actually lands in: the operator's first click
    // opened the CEO planning round on this sub, and a second click (impatience, or the epic's own button clicked
    // twice) fires before that round is even approved.
    [InlineData("AC-1", true)]
    [InlineData("AC-2", false)]
    public void HasRunInFlight_ReadsTheSharedPlanningDraft(string queried, bool inFlight)
    {
        var (planController, queue, manager) = Fresh();
        planController.BeginPlanning(Plan("youtrack", "AC-1"));

        Assert.Equal(inFlight, AutopilotPlugin._HasRunInFlight(planController, queue, manager, "youtrack", queried));
    }

    [Theory]
    // Approved and waiting for a free slot (MaxConcurrentRuns already full) — still in flight, not settled.
    [InlineData("youtrack", true)]
    // Two trackers could each carry an "AC-1"-shaped id in principle — the guard must not cross-match them.
    [InlineData("github-issues", false)]
    public void HasRunInFlight_ReadsTheQueue_MatchingOnTrackerAndIssue_NotIssueAlone(string queuedTracker, bool inFlight)
    {
        var (planController, queue, manager) = Fresh();
        queue.Enqueue(Plan(queuedTracker, "AC-1"));

        Assert.Equal(inFlight, AutopilotPlugin._HasRunInFlight(planController, queue, manager, "youtrack", "AC-1"));
    }
}
