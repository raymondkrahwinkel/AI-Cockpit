using System.Text.Json;
using Cockpit.Plugins.Abstractions;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// The run manager (AC-174): it runs approved plans from the queue up to the concurrency cap, queues the rest, and
/// starts the next as soon as a running one frees a slot. Starting a run is injected, so the concurrency logic is tested
/// without live sessions.
/// </summary>
public class AutopilotRunManagerTests
{
    private sealed class FakeStorage : IPluginStorage
    {
        private readonly Dictionary<string, string> _data = new(StringComparer.Ordinal);
        public T? Get<T>(string key) => _data.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : default;
        public void Set<T>(string key, T value) => _data[key] = JsonSerializer.Serialize(value);
        public void SetSecret(string key, string value) => Set(key, value);
        public string? GetSecret(string key) => Get<string>(key);
    }

    private static AutopilotPlan _Plan(string goal) => new(goal, null, [new AutopilotStep("1", "S", "d", "work", "opus", "b", "a")]);

    private static AutopilotRunCoordinator _Coordinator() => new(Substitute.For<ICockpitHost>(), new AutopilotPlanController());

    [Fact]
    public async Task Submit_RunsUpToTheCap_QueuesTheRest_ThenStartsTheNextWhenOneFinishes()
    {
        var storage = new FakeStorage();
        var settings = new AutopilotSettings(storage);
        settings.SetMaxConcurrentRuns(2);
        var queue = new AutopilotRunQueue(storage);

        var started = new List<string>();
        var gates = new Dictionary<string, TaskCompletionSource>();
        AutopilotRunHandle Start(AutopilotPlan plan)
        {
            started.Add(plan.Goal);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gates[plan.Goal] = gate;
            return new AutopilotRunHandle(_Coordinator(), gate.Task);
        }

        var manager = new AutopilotRunManager(queue, settings, Start);
        manager.Submit(_Plan("a"));
        manager.Submit(_Plan("b"));
        manager.Submit(_Plan("c"));

        // Cap is 2: a and b run, c waits its turn.
        started.Should().ContainInOrder("a", "b");
        started.Should().NotContain("c");
        manager.Active.Should().HaveCount(2);
        queue.Count.Should().Be(1);

        // a finishes → its slot frees → c starts (b and c now running).
        gates["a"].SetResult();
        await _Eventually(() => started.Contains("c") && manager.Active.Count == 2);

        queue.Count.Should().Be(0);
    }

    [Fact]
    public async Task WithACapOfOne_RunsAreStrictlySerial()
    {
        var storage = new FakeStorage();
        var settings = new AutopilotSettings(storage); // default cap 1
        var queue = new AutopilotRunQueue(storage);

        var started = new List<string>();
        var gates = new Dictionary<string, TaskCompletionSource>();
        AutopilotRunHandle Start(AutopilotPlan plan)
        {
            started.Add(plan.Goal);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gates[plan.Goal] = gate;
            return new AutopilotRunHandle(_Coordinator(), gate.Task);
        }

        var manager = new AutopilotRunManager(queue, settings, Start);
        manager.Submit(_Plan("a"));
        manager.Submit(_Plan("b"));

        started.Should().ContainInOrder("a");
        started.Should().NotContain("b");

        gates["a"].SetResult();
        await _Eventually(() => started.Contains("b") && manager.Active.Count == 1);
    }

    private static async Task _Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        condition().Should().BeTrue("the condition should hold within the timeout");
    }
}
