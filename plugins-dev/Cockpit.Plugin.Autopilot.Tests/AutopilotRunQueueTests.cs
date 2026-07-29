using System.Text.Json;
using Cockpit.Plugins.Abstractions;
using Cockpit.TestSupport;

namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// The queue of approved runs waiting to execute (AC-174): it holds plans in run order, hands the front one out, lets the
/// operator reorder or drop entries, and survives a restart because it persists through the plugin's storage.
/// </summary>
public class AutopilotRunQueueTests
{
    /// <summary>An in-memory <see cref="IPluginStorage"/> that round-trips through JSON, the way the host's real storage does — so persistence is exercised for real.</summary>
    private sealed class FakeStorage : IPluginStorage
    {
        private readonly Dictionary<string, string> _data = new(StringComparer.Ordinal);

        public T? Get<T>(string key) => _data.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : default;

        public void Set<T>(string key, T value) => _data[key] = JsonSerializer.Serialize(value);

        public void SetSecret(string key, string value) => Set(key, value);

        public string? GetSecret(string key) => Get<string>(key);
    }

    private static AutopilotPlan _Plan(string goal) =>
        new(goal, null, [new AutopilotStep("1", "Step", "desc", "work", "opus", "brief", "compiles", GateMode.Hard)]);

    [Fact]
    public void Enqueue_KeepsRunOrder_AndDequeueTakesTheFront()
    {
        var queue = new AutopilotRunQueue(new FakeStorage());
        queue.Enqueue(_Plan("first"));
        queue.Enqueue(_Plan("second"));

        Assert.Equal(2, queue.Count);
        Assert.True(SequenceAssert.ContainsInOrder(queue.Items.Select(plan => plan.Goal), "first", "second"));

        Assert.True(queue.TryDequeue(out var front));
        Assert.Equal("first", front!.Goal);
        Assert.True(SequenceAssert.ContainsInOrder(queue.Items.Select(plan => plan.Goal), "second"));
    }

    [Fact]
    public void TryDequeue_OnAnEmptyQueue_IsFalse()
    {
        var queue = new AutopilotRunQueue(new FakeStorage());

        Assert.False(queue.TryDequeue(out var plan));
        Assert.Null(plan);
    }

    [Fact]
    public void MoveUp_MoveDown_And_RemoveAt_ReorderTheQueue()
    {
        var queue = new AutopilotRunQueue(new FakeStorage());
        queue.Enqueue(_Plan("a"));
        queue.Enqueue(_Plan("b"));
        queue.Enqueue(_Plan("c"));

        queue.MoveUp(2); // c ahead of b
        Assert.True(SequenceAssert.ContainsInOrder(queue.Items.Select(plan => plan.Goal), "a", "c", "b"));

        queue.MoveDown(0); // a after c
        Assert.True(SequenceAssert.ContainsInOrder(queue.Items.Select(plan => plan.Goal), "c", "a", "b"));

        queue.RemoveAt(1); // drop a
        Assert.True(SequenceAssert.ContainsInOrder(queue.Items.Select(plan => plan.Goal), "c", "b"));

        // Out-of-range operations are no-ops rather than throwing.
        queue.MoveUp(0);
        queue.MoveDown(1);
        queue.RemoveAt(9);
        Assert.True(SequenceAssert.ContainsInOrder(queue.Items.Select(plan => plan.Goal), "c", "b"));
    }

    [Fact]
    public void TheQueueSurvivesARestart_ThroughPersistence()
    {
        var storage = new FakeStorage();
        var queue = new AutopilotRunQueue(storage);
        queue.Enqueue(_Plan("keep me"));
        queue.Enqueue(_Plan("and me"));

        // A fresh queue over the same storage is the restart: the staged plans come back, in order and with their steps.
        var restored = new AutopilotRunQueue(storage);

        Assert.Equal(2, restored.Count);
        Assert.True(SequenceAssert.ContainsInOrder(restored.Items.Select(plan => plan.Goal), "keep me", "and me"));
        Assert.Equal("Step", Assert.Single(restored.Items[0].Steps).Title);
    }

    [Fact]
    public void Changed_FiresOnEveryMutation()
    {
        var queue = new AutopilotRunQueue(new FakeStorage());
        var fired = 0;
        queue.Changed += () => fired++;

        queue.Enqueue(_Plan("a"));
        queue.Enqueue(_Plan("b"));
        queue.MoveUp(1);
        queue.RemoveAt(0);
        queue.TryDequeue(out _);

        Assert.Equal(5, fired);
    }
}
