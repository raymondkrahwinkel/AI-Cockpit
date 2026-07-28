using System.Text.Json;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// AC-347 backward compatibility: the new fields on <see cref="AutopilotRunRecord"/>/<see cref="AutopilotRunStepRecord"/>
/// round-trip through storage, and — the case that actually matters — JSON persisted before this change (no
/// Attempts/Reworks/Correction/RunId/Ticket/BlockadeAnswers/PullRequestMissing at all) still deserializes, reading back
/// the new fields' defaults.
/// </summary>
public class AutopilotRunHistoryCompatibilityTests
{
    /// <summary>An in-memory <see cref="IPluginStorage"/> that round-trips through JSON, the way the host's real storage does.</summary>
    private sealed class FakeStorage : IPluginStorage
    {
        private readonly Dictionary<string, string> _data = new(StringComparer.Ordinal);

        public T? Get<T>(string key) => _data.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : default;

        public void Set<T>(string key, T value) => _data[key] = JsonSerializer.Serialize(value);

        public void SetSecret(string key, string value) => Set(key, value);

        public string? GetSecret(string key) => Get<string>(key);
    }

    [Fact]
    public void RunRecord_WithTheNewFields_RoundTripsThroughStorage_Identically()
    {
        var storage = new FakeStorage();
        var history = new AutopilotRunHistory(storage);
        var step = new AutopilotRunStepRecord("Code", AutopilotStepStatus.Passed, string.Empty)
        {
            Attempts = 2,
            Reworks = 1,
            Correction = AutopilotCorrectionKind.ReviewFinding,
            CorrectionSource = AutopilotCorrectionSource.Operator,
        };
        var record = new AutopilotRunRecord("run", "goal", AutopilotPlanPhase.MergeReady, null, "2026-07-28T00:00:00+00:00", [step])
        {
            RunId = "run-id-1",
            Ticket = "AC-347",
            BlockadeAnswers = 3,
            PullRequestMissing = true,
        };
        history.Add(record);

        var restored = new AutopilotRunHistory(storage);

        var restoredRecord = Assert.Single(restored.Items);
        Assert.Equal("run-id-1", restoredRecord.RunId);
        Assert.Equal("AC-347", restoredRecord.Ticket);
        Assert.Equal(3, restoredRecord.BlockadeAnswers);
        Assert.True(restoredRecord.PullRequestMissing);
        var restoredStep = Assert.Single(restoredRecord.Steps);
        Assert.Equal(2, restoredStep.Attempts);
        Assert.Equal(1, restoredStep.Reworks);
        Assert.Equal(AutopilotCorrectionKind.ReviewFinding, restoredStep.Correction);
        Assert.Equal(AutopilotCorrectionSource.Operator, restoredStep.CorrectionSource);
    }

    [Fact]
    public void PreAC347Json_WithNoneOfTheNewFields_StillDeserializes_WithDefaults()
    {
        // The exact shape AutopilotRunRecord/AutopilotRunStepRecord had before AC-347: no Attempts, Correction,
        // CorrectionSource, RunId, Ticket or BlockadeAnswers anywhere. Enum values are the pre-existing underlying ints
        // (AutopilotStepStatus.Passed = 2, AutopilotPlanPhase.MergeReady = 4) — System.Text.Json's default numeric
        // enum encoding, the same one Set<T> above writes with.
        const string legacyJson = """
            [
              {
                "Name": "legacy-run",
                "Goal": "goal for legacy-run",
                "Outcome": 4,
                "BlockReason": null,
                "FinishedAt": "2026-01-01T00:00:00+00:00",
                "Steps": [
                  { "Title": "Code", "Status": 2, "Note": "" }
                ]
              }
            ]
            """;

        var records = JsonSerializer.Deserialize<List<AutopilotRunRecord>>(legacyJson);

        Assert.NotNull(records);
        var record = Assert.Single(records);
        Assert.Equal("legacy-run", record.Name);
        Assert.Equal(AutopilotPlanPhase.MergeReady, record.Outcome);
        Assert.Equal(string.Empty, record.RunId);
        Assert.Equal(string.Empty, record.Ticket);
        Assert.Equal(0, record.BlockadeAnswers);
        Assert.False(record.PullRequestMissing);

        var step = Assert.Single(record.Steps);
        Assert.Equal("Code", step.Title);
        Assert.Equal(AutopilotStepStatus.Passed, step.Status);
        Assert.Equal(0, step.Attempts);
        Assert.Equal(0, step.Reworks);
        Assert.Equal(AutopilotCorrectionKind.None, step.Correction);
        Assert.Equal(AutopilotCorrectionSource.Automatic, step.CorrectionSource);
    }
}
