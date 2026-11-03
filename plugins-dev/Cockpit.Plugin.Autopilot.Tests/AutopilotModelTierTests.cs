using Cockpit.Plugins.Abstractions.Profiles;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.Autopilot.Tests;

// The cost ceiling (AC-256). These pin the two things a ceiling has to get right: that it actually turns a plan down,
// and that it stays silent about everything it cannot fairly judge — otherwise it either does nothing or blocks work
// on a guess. The strategies are an internal enum, so the rows box them and xUnit names each case after it.
public class AutopilotModelTierTests
{
    private static readonly IReadOnlyList<PluginProfileInfo> Roster =
    [
        new PluginProfileInfo("Claude", "Plugin", string.Empty)
        {
            ModelSuggestions = ["fable", "opus", "sonnet", "haiku"],
            ModelCostEstimatesCheapestFirst =
            [
                new PluginModelCostEstimate("haiku") { EstimatedInputUsdPerMillionTokens = 1m, EstimatedOutputUsdPerMillionTokens = 5m },
                new PluginModelCostEstimate("sonnet") { EstimatedInputUsdPerMillionTokens = 3m, EstimatedOutputUsdPerMillionTokens = 15m },
                new PluginModelCostEstimate("opus") { EstimatedInputUsdPerMillionTokens = 5m, EstimatedOutputUsdPerMillionTokens = 25m },
                new PluginModelCostEstimate("fable") { EstimatedInputUsdPerMillionTokens = 10m, EstimatedOutputUsdPerMillionTokens = 50m },
            ],
        },
    ];

    private static AutopilotStep Step(string? model, bool reviewGate = false, string profile = "Claude") =>
        new("build", "Build it", string.Empty, profile, model, "brief", null) { IsReviewGate = reviewGate };

    public static IEnumerable<object[]> HeldToTheCeiling() =>
    [
        // Not the cheapest: the point is to stay within budget, not to strip the step of every capability it may need.
        [AutopilotCostStrategy.Balanced, "fable", "sonnet"],
        // A step already within the ceiling is left exactly where the CEO put it.
        [AutopilotCostStrategy.Balanced, "haiku", "haiku"],
        [AutopilotCostStrategy.QualityFirst, "fable", "fable"],
    ];

    [Theory]
    [MemberData(nameof(HeldToTheCeiling))]
    public void HoldToCeiling_LandsOnTheDearestModelStillAllowed(object costStrategy, string model, string expected) =>
        Assert.Equal(expected, AutopilotModelTier.HoldToCeiling(Step(model), Roster, (AutopilotCostStrategy)costStrategy).Model);

}
