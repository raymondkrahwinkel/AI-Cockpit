using System.Text.Json;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Autopilot.Tests;

// AC-347: an operator reclassifying a settled step writes back through `AutopilotRunHistory.Replace`. The
// case worth pinning is the one a position-keyed write gets silently wrong — a newer run settling in between shifts
// every entry down one, and the edit must still land on the run it was opened on.
public class AutopilotRunHistoryReplaceTests
{
    private sealed class FakeStorage : IPluginStorage
    {
        private readonly Dictionary<string, string> _data = new(StringComparer.Ordinal);

        public T? Get<T>(string key) => _data.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : default;

        public void Set<T>(string key, T value) => _data[key] = JsonSerializer.Serialize(value);

        public void SetSecret(string key, string value) => Set(key, value);

        public string? GetSecret(string key) => Get<string>(key);
    }

    private static AutopilotRunRecord Run(string name) =>
        new(name, "goal", AutopilotPlanPhase.MergeReady, null, "2026-07-28T00:00:00+00:00",
            [new AutopilotRunStepRecord("Code", AutopilotStepStatus.Passed, string.Empty)]);

    [Fact]
    public void Replace_AfterANewerRunSettled_EditsTheRunItWasOpenedOn_NotTheOneAtThatPosition()
    {
        var history = new AutopilotRunHistory(new FakeStorage());
        var older = Run("older");
        history.Add(older);
        history.Add(Run("newer"));

        history.Replace(older, older with { Ticket = "AC-347" });

        Assert.Equal("newer", history.Items[0].Name);
        Assert.Equal(string.Empty, history.Items[0].Ticket);
        Assert.Equal("older", history.Items[1].Name);
        Assert.Equal("AC-347", history.Items[1].Ticket);
    }

    [Fact]
    public void Replace_WithARecordTheHistoryNoLongerHolds_ChangesNothing()
    {
        var history = new AutopilotRunHistory(new FakeStorage());
        var dropped = Run("dropped");
        history.Add(dropped);
        history.Clear();

        history.Replace(dropped, dropped with { Ticket = "AC-347" });

        Assert.Empty(history.Items);
    }

    [Fact]
    public void Replace_PersistsThroughStorage_SoTheEditSurvivesARestart()
    {
        var storage = new FakeStorage();
        var history = new AutopilotRunHistory(storage);
        var record = Run("run");
        history.Add(record);

        history.Replace(record, record with { Ticket = "AC-347" });

        Assert.Equal("AC-347", new AutopilotRunHistory(storage).Items[0].Ticket);
    }

    [Fact]
    public void Replace_MatchesByInstance_NotByContent_WhenTwoRecordsAreValueEqual()
    {
        // The XML doc on Replace promises matching "on the record instance, deliberately not on a position" — but the
        // doc's actual guarantee is instance identity, full stop, not merely "not position". Two distinct instances
        // that are content-identical (same name/goal/outcome/FinishedAt, and the very same Steps list reference so the
        // record's own value-equality genuinely holds — a different list reference would already fail Equals on its
        // own and prove nothing) pin that down. The duplicate is added after the target, so it lands in front of it in
        // the newest-first list; a value-based (Equals) scan would hit the duplicate first and silently edit the wrong
        // run — the exact failure the "not on a position" note is guarding against, just one instance-identity step
        // further than a plain position mix-up.
        var history = new AutopilotRunHistory(new FakeStorage());
        var sharedSteps = new List<AutopilotRunStepRecord> { new("Code", AutopilotStepStatus.Passed, string.Empty) };
        var target = new AutopilotRunRecord("run", "goal", AutopilotPlanPhase.MergeReady, null, "2026-07-28T00:00:00+00:00", sharedSteps);
        var duplicate = new AutopilotRunRecord("run", "goal", AutopilotPlanPhase.MergeReady, null, "2026-07-28T00:00:00+00:00", sharedSteps);
        Assert.Equal(target, duplicate); // sanity check: genuinely value-equal, not just superficially similar
        Assert.NotSame(target, duplicate);

        history.Add(target);
        history.Add(duplicate); // inserted at the front — the list is now [duplicate, target]

        history.Replace(target, target with { Ticket = "AC-347" });

        Assert.Equal(string.Empty, history.Items[0].Ticket); // duplicate — untouched despite matching target by content
        Assert.Equal("AC-347", history.Items[1].Ticket); // target — correctly identified by instance
    }
}
