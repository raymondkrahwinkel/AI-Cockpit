using System.Text.Json;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Profiles;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Plugin.Autopilot.Tests;

// The cost ceiling as the CEO meets it (AC-256): through `AutopilotPlanTools.SetPlan`, not the rule class.
// `AutopilotModelTierTests` proves the rule decides correctly; these prove it is actually consulted when
// a plan is emitted — delete that call and the rule stays green while nothing enforces it.
public class AutopilotPlanTierGateTests
{
    private static readonly IReadOnlyList<PluginProfileInfo> Roster =
    [
        new PluginProfileInfo("Claude", "Plugin", string.Empty)
        {
            ModelSuggestions = ["fable", "opus", "sonnet", "haiku"],
            ModelCostEstimatesCheapestFirst =
            [
                new PluginModelCostEstimate("haiku"),
                new PluginModelCostEstimate("sonnet"),
                new PluginModelCostEstimate("opus"),
                new PluginModelCostEstimate("fable"),
            ],
        },
    ];

    [Fact]
    public async Task SetPlan_TurnsDownAStepAboveTheCostCeiling()
    {
        var tools = _PlanningTools();

        var result = await tools.SetPlan(
            "Ship it",
            """[{"id":"1","title":"Code","profile":"Claude","model":"opus","brief":"b","hard":true}]""");

        Assert.False(_Ok(result));
        Assert.Contains("opus", result, StringComparison.Ordinal);
        Assert.Contains("haiku", result, StringComparison.Ordinal);
        Assert.Contains("sonnet", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetPlan_AcceptsTheSameStepOnAModelWithinTheCeiling()
    {
        var tools = _PlanningTools();

        var result = await tools.SetPlan(
            "Ship it",
            """[{"id":"1","title":"Code","profile":"Claude","model":"sonnet","brief":"b","hard":true}]""");

        Assert.True(_Ok(result));
    }

    [Fact]
    public async Task SetPlan_LeavesAReviewGateOnTheDearestModel()
    {
        // The ceiling stops at review gates on purpose, and that has to survive the round trip through the tool —
        // capping them would trade a cheaper run for missed findings.
        var tools = _PlanningTools();

        var result = await tools.SetPlan(
            "Ship it",
            """[{"id":"1","title":"Security review","profile":"Claude","model":"fable","brief":"b","reviewGate":true}]""");

        Assert.True(_Ok(result));
    }

    private static AutopilotPlanTools _PlanningTools()
    {
        var host = Substitute.For<ICockpitHost>();
        host.GetProfilesAsync().Returns(Task.FromResult(Roster));
        host.CurrentMcpCallerPaneId.Returns("pane-1");
        host.TrackerProviders.Returns([]);

        var controller = new AutopilotPlanController();
        controller.BeginPlanning(AutopilotPlan.Empty(source: null, goal: "Ship it"));
        controller.BindSession("pane-1");

        // Empty storage, so the run sits on the default Balanced strategy — the stand a plan is held to unless the
        // operator has moved it, which is the case the ceiling has to get right.
        return new AutopilotPlanTools(host, controller, new AutopilotSettings(new FakeStorage()));
    }

    private static bool _Ok(string result) =>
        JsonDocument.Parse(result).RootElement.GetProperty("ok").GetBoolean();

    private sealed class FakeStorage : IPluginStorage
    {
        private readonly Dictionary<string, string> _data = new(StringComparer.Ordinal);

        public T? Get<T>(string key) => _data.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : default;

        public void Set<T>(string key, T value) => _data[key] = JsonSerializer.Serialize(value);

        public void SetSecret(string key, string value) => Set(key, value);

        public string? GetSecret(string key) => Get<string>(key);
    }
}
