using System.Text.Json;
using Cockpit.Plugins.Abstractions;
using Cockpit.TestSupport;

namespace Cockpit.Plugin.Autopilot.Tests;

// The history of settled runs: a finished run is recorded here newest-first so it does not vanish
// from the surface, survives a restart through the plugin's storage, and is capped so it cannot grow without bound.
public class AutopilotRunHistoryTests
{
    // An in-memory `IPluginStorage` that round-trips through JSON, the way the host's real storage does.
    private sealed class FakeStorage : IPluginStorage
    {
        private readonly Dictionary<string, string> _data = new(StringComparer.Ordinal);

        public T? Get<T>(string key) => _data.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : default;

        public void Set<T>(string key, T value) => _data[key] = JsonSerializer.Serialize(value);

        public void SetSecret(string key, string value) => Set(key, value);

        public string? GetSecret(string key) => Get<string>(key);
    }

    private static AutopilotRunRecord _Record(string name, AutopilotPlanPhase outcome = AutopilotPlanPhase.MergeReady) =>
        new(name, $"goal for {name}", outcome, outcome == AutopilotPlanPhase.Blocked ? "a hard step failed" : null, "2026-07-22T18:00:00+02:00",
            [new AutopilotRunStepRecord("Code", AutopilotStepStatus.Passed, string.Empty)]);

    [Fact]
    public void Add_PutsNewestFirst()
    {
        var history = new AutopilotRunHistory(new FakeStorage());
        history.Add(_Record("first"));
        history.Add(_Record("second"));

        Assert.Equal(2, history.Count);
        Assert.True(SequenceAssert.ContainsInOrder(history.Items.Select(record => record.Name), "second", "first"));
    }

    [Fact]
    public void History_SurvivesARestart_ThroughPersistence()
    {
        var storage = new FakeStorage();
        var history = new AutopilotRunHistory(storage);
        history.Add(_Record("kept", AutopilotPlanPhase.Blocked));

        // A fresh history over the same storage is the restart: the record comes back with its outcome and steps intact.
        var restored = new AutopilotRunHistory(storage);

        Assert.Equal(1, restored.Count);
        var record = restored.Items[0];
        Assert.Equal("kept", record.Name);
        Assert.Equal(AutopilotPlanPhase.Blocked, record.Outcome);
        Assert.Equal("a hard step failed", record.BlockReason);
        Assert.Equal(AutopilotStepStatus.Passed, Assert.Single(record.Steps).Status);
    }

    [Fact]
    public void Add_CapsAtFifty_DroppingTheOldest()
    {
        var history = new AutopilotRunHistory(new FakeStorage());
        for (var i = 0; i < 55; i++)
        {
            history.Add(_Record($"run-{i}"));
        }

        Assert.Equal(50, history.Count);
        // The newest is at the front; the five oldest (run-0..run-4) fell off the end.
        Assert.Equal("run-54", history.Items[0].Name);
        Assert.DoesNotContain("run-4", history.Items.Select(record => record.Name));
        Assert.Contains("run-5", history.Items.Select(record => record.Name));
    }

    [Fact]
    public void Clear_EmptiesHistory_AndFiresOnceWhenNonEmpty()
    {
        var history = new AutopilotRunHistory(new FakeStorage());
        var fired = 0;
        history.Changed += () => fired++;

        history.Add(_Record("a")); // 1
        history.Clear();           // 2
        history.Clear();           // no-op — already empty, does not fire

        Assert.Equal(0, history.Count);
        Assert.Equal(2, fired);
    }
}
