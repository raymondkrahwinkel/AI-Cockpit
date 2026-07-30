using System.Text.Json;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// AC-346 review, MEDIUM 6: a second click on an epic (or a plain issue) while its run is already in flight must not
/// start a duplicate — a second worktree and a second PR on the same ticket. Exercises
/// <see cref="AutopilotPlugin._HasRunInFlight"/> directly against real (not mocked) <see cref="AutopilotPlanController"/>
/// and <see cref="AutopilotRunQueue"/> — the two places a not-yet-started run can be waiting, and the easiest to
/// construct without a UI or a live embedded session. The third place (<see cref="AutopilotRunManager.Active"/> —
/// a run genuinely executing right now) is covered by review rather than a direct test here: constructing an active
/// coordinator needs a started run, which needs a host and an embedded session this project's other coordinator tests
/// build through NSubstitute + AutopilotRunCoordinatorTests' own scaffolding rather than duplicating it here.
/// </summary>
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

    [Fact]
    public void HasRunInFlight_WhileTheSameIssueIsStillOnTheSharedPlanningDraft_IsTrue()
    {
        // The window a double-click on the tracker button most often actually lands in: the operator's first click
        // opened the CEO planning round on this sub, and a second click (impatience, or the epic's own button clicked
        // twice) fires before that round is even approved.
        var (planController, queue, manager) = Fresh();
        planController.BeginPlanning(Plan("youtrack", "AC-1"));

        Assert.True(AutopilotPlugin._HasRunInFlight(planController, queue, manager, "youtrack", "AC-1"));
    }

    [Fact]
    public void HasRunInFlight_ForADifferentIssueOnTheDraft_IsFalse()
    {
        var (planController, queue, manager) = Fresh();
        planController.BeginPlanning(Plan("youtrack", "AC-1"));

        Assert.False(AutopilotPlugin._HasRunInFlight(planController, queue, manager, "youtrack", "AC-2"));
    }

    [Fact]
    public void HasRunInFlight_WhileQueuedBehindOthers_IsTrue()
    {
        // Approved and waiting for a free slot (MaxConcurrentRuns already full) — still in flight, not settled.
        var (planController, queue, manager) = Fresh();
        queue.Enqueue(Plan("youtrack", "AC-1"));

        Assert.True(AutopilotPlugin._HasRunInFlight(planController, queue, manager, "youtrack", "AC-1"));
    }

    [Fact]
    public void HasRunInFlight_MatchesOnTrackerAndIssue_NotIssueAlone()
    {
        // Two trackers could each carry an "AC-1"-shaped id in principle — the guard must not cross-match them.
        var (planController, queue, manager) = Fresh();
        queue.Enqueue(Plan("github-issues", "AC-1"));

        Assert.False(AutopilotPlugin._HasRunInFlight(planController, queue, manager, "youtrack", "AC-1"));
    }
}
