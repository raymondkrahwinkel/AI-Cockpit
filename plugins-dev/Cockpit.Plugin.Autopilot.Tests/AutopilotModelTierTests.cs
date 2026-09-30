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
                new PluginModelCostEstimate("haiku"),
                new PluginModelCostEstimate("sonnet"),
                new PluginModelCostEstimate("opus"),
                new PluginModelCostEstimate("fable"),
            ],
        },
    ];

    private static AutopilotStep Step(string? model, bool reviewGate = false, string profile = "Claude") =>
        new("build", "Build it", string.Empty, profile, model, "brief", null) { IsReviewGate = reviewGate };

    public static IEnumerable<object[]> WithinTheCeiling() =>
    [
        [AutopilotCostStrategy.Balanced, "haiku", false],
        [AutopilotCostStrategy.Balanced, "sonnet", false],
        [AutopilotCostStrategy.CostFirst, "haiku", false],
        // Quality-first imposes no ceiling at all, so even the dearest model passes.
        [AutopilotCostStrategy.QualityFirst, "fable", false],
        // A gate that misses a finding costs more than the tokens it saved, so the ceiling deliberately stops at
        // review gates — the same model the row below is refused on.
        [AutopilotCostStrategy.Balanced, "fable", true],
    ];

    public static IEnumerable<object[]> AboveTheCeiling() =>
    [
        [AutopilotCostStrategy.Balanced, "opus"],
        [AutopilotCostStrategy.Balanced, "fable"],
        // Cost-first allows only the cheapest of the ranking, so the second-cheapest is already over.
        [AutopilotCostStrategy.CostFirst, "sonnet"],
    ];

    [Theory]
    [MemberData(nameof(AboveTheCeiling))]
    public void Validate_AModelAboveTheCeiling_IsRefused(object costStrategy, string model) =>
        Assert.NotNull(AutopilotModelTier.Validate(Step(model), Roster, (AutopilotCostStrategy)costStrategy));

    public static IEnumerable<object[]> NothingToJudge() =>
    [
        // A local profile pins its own model and the step leaves it empty — there is nothing to place on the scale.
        [null!, "Claude"],
        // The profile gate (AC-210) already refuses an unknown profile; the ceiling must not produce a second,
        // confusing message on top of it.
        ["fable", "Nope"],
    ];

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

    [Fact]
    public void HoldToCeiling_NeverMovesAStepOntoAModelTheProfileDoesNotOffer()
    {
        // A provider may price a model it does not list. Moving the step there would swap a cost problem for a step
        // that dies at launch on the profile check, so it stays where it is instead.
        IReadOnlyList<PluginProfileInfo> pricedButNotOffered =
        [
            new PluginProfileInfo("Claude", "Plugin", string.Empty)
            {
                ModelSuggestions = ["opus"],
                ModelCostEstimatesCheapestFirst = [new PluginModelCostEstimate("haiku"), new PluginModelCostEstimate("opus")],
            },
        ];

        Assert.Equal("opus", AutopilotModelTier.HoldToCeiling(Step("opus"), pricedButNotOffered, AutopilotCostStrategy.CostFirst).Model);
    }

    // A fraction rather than a fixed index, so a provider offering two models is not held to a four-model rule — and
    // never zero, or a profile with one model could run nothing at all.
    public static IEnumerable<object[]> AllowedCounts() =>
    [
        [4, AutopilotCostStrategy.Balanced, 2],
        [5, AutopilotCostStrategy.Balanced, 2],
        [2, AutopilotCostStrategy.Balanced, 1],
        [1, AutopilotCostStrategy.Balanced, 1],
        [4, AutopilotCostStrategy.CostFirst, 1],
        [1, AutopilotCostStrategy.CostFirst, 1],
        [4, AutopilotCostStrategy.QualityFirst, 4],
    ];
}
