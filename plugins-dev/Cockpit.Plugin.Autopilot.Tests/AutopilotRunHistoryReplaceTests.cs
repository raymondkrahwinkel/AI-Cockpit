using System.Text.Json;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// AC-347: an operator reclassifying a settled step writes back through <see cref="AutopilotRunHistory.Replace"/>. The
/// case worth pinning is the one a position-keyed write gets silently wrong — a newer run settling in between shifts
/// every entry down one, and the edit must still land on the run it was opened on.
/// </summary>
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
}
