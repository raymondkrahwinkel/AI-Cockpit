using Cockpit.Plugins.Abstractions.Profiles;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.Autopilot.Tests;

// The cost ceiling (AC-256). These pin the two things a ceiling has to get right: that it actually turns a plan down,
// and that it stays silent about everything it cannot fairly judge — otherwise it either does nothing or blocks work
// on a guess.
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

    [Theory]
    [InlineData("haiku")]
    [InlineData("sonnet")]
    public void Validate_UnderBalanced_AcceptsTheCheaperHalf(string model) =>
        Assert.Null(AutopilotModelTier.Validate(Step(model), Roster, AutopilotCostStrategy.Balanced));

    [Theory]
    [InlineData("opus")]
    [InlineData("fable")]
    public void Validate_UnderBalanced_RefusesTheDearerHalf(string model) =>
        Assert.NotNull(AutopilotModelTier.Validate(Step(model), Roster, AutopilotCostStrategy.Balanced));

    [Fact]
    public void Validate_UnderBalanced_LeavesAReviewGateOnTheDearestModel()
    {
        // A gate that misses a finding costs more than the tokens it saved, so the ceiling deliberately stops at them.
        Assert.NotNull(AutopilotModelTier.Validate(Step("fable"), Roster, AutopilotCostStrategy.Balanced));
        Assert.Null(AutopilotModelTier.Validate(Step("fable", reviewGate: true), Roster, AutopilotCostStrategy.Balanced));
    }

    [Fact]
    public void Validate_UnderCostFirst_AllowsOnlyTheCheapest()
    {
        Assert.Null(AutopilotModelTier.Validate(Step("haiku"), Roster, AutopilotCostStrategy.CostFirst));
        Assert.NotNull(AutopilotModelTier.Validate(Step("sonnet"), Roster, AutopilotCostStrategy.CostFirst));
    }

    [Fact]
    public void Validate_UnderQualityFirst_ImposesNoCeiling() =>
        Assert.Null(AutopilotModelTier.Validate(Step("fable"), Roster, AutopilotCostStrategy.QualityFirst));

    [Fact]
    public void Validate_Refusal_NamesTheOffenderAndEveryModelTheStepMayUseInstead()
    {
        // The CEO has to be able to fix this in one redraft, which it cannot do from "too expensive" alone.
        var refusal = AutopilotModelTier.Validate(Step("opus"), Roster, AutopilotCostStrategy.Balanced);

        Assert.NotNull(refusal);
        Assert.Contains("build", refusal);
        Assert.Contains("opus", refusal);
        Assert.Contains("haiku", refusal);
        Assert.Contains("sonnet", refusal);
    }

    [Fact]
    public void Validate_WhenTheProviderRankedNothing_JudgesNothing()
    {
        IReadOnlyList<PluginProfileInfo> unranked = [new PluginProfileInfo("Codex", "Plugin", string.Empty) { ModelSuggestions = ["cheap", "dear"] }];

        Assert.Null(AutopilotModelTier.Validate(Step("dear", profile: "Codex"), unranked, AutopilotCostStrategy.CostFirst));
    }

    [Fact]
    public void Validate_ModelTheProfileOffersButDidNotPrice_IsNotWavedThrough()
    {
        // The hole this closes: a provider that priced only its cheap models would otherwise opt its dear ones out of
        // the ceiling entirely, since the profile gate accepts anything in ModelSuggestions and this one used to pass
        // anything outside the ranking. The allowed set is "the cheapest N of the ranking", so outside it is outside.
        IReadOnlyList<PluginProfileInfo> partlyPriced =
        [
            new PluginProfileInfo("Claude", "Plugin", string.Empty)
            {
                ModelSuggestions = ["fable", "sonnet", "haiku"],
                ModelCostEstimatesCheapestFirst = [new PluginModelCostEstimate("haiku"), new PluginModelCostEstimate("sonnet")],
            },
        ];

        var refusal = AutopilotModelTier.Validate(Step("fable"), partlyPriced, AutopilotCostStrategy.CostFirst);

        Assert.NotNull(refusal);
        Assert.Contains("has not", refusal);
        Assert.Contains("haiku", refusal);
    }

    [Fact]
    public void HoldToCeiling_MovesAnOverCeilingStepToTheDearestModelStillAllowed() =>
        // Not the cheapest: the point is to stay within budget, not to strip the step of every capability it may need.
        Assert.Equal("sonnet", AutopilotModelTier.HoldToCeiling(Step("fable"), Roster, AutopilotCostStrategy.Balanced).Model);

    [Fact]
    public void HoldToCeiling_LeavesAStepThatIsAlreadyWithinTheCeiling()
    {
        Assert.Equal("haiku", AutopilotModelTier.HoldToCeiling(Step("haiku"), Roster, AutopilotCostStrategy.Balanced).Model);
        Assert.Equal("fable", AutopilotModelTier.HoldToCeiling(Step("fable"), Roster, AutopilotCostStrategy.QualityFirst).Model);
    }

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

    [Fact]
    public void HoldToCeiling_LeavesAnythingItCannotJudge()
    {
        IReadOnlyList<PluginProfileInfo> unranked = [new PluginProfileInfo("Codex", "Plugin", string.Empty) { ModelSuggestions = ["dear"] }];

        Assert.Equal("dear", AutopilotModelTier.HoldToCeiling(Step("dear", profile: "Codex"), unranked, AutopilotCostStrategy.CostFirst).Model);
        Assert.Equal("fable", AutopilotModelTier.HoldToCeiling(Step("fable", reviewGate: true), Roster, AutopilotCostStrategy.CostFirst).Model);
    }

    [Fact]
    public void Validate_StepWithoutAModel_JudgesNothing() =>
        // A local profile pins its own model and the step leaves it empty — there is nothing to place on the scale.
        Assert.Null(AutopilotModelTier.Validate(Step(null), Roster, AutopilotCostStrategy.CostFirst));

    [Fact]
    public void Validate_UnknownProfile_JudgesNothing() =>
        // The profile gate (AC-210) already refuses this; the ceiling must not produce a second, confusing message.
        Assert.Null(AutopilotModelTier.Validate(Step("fable", profile: "Nope"), Roster, AutopilotCostStrategy.Balanced));

    [Fact]
    public void AllowedCount_ScalesWithTheRosterAndNeverStrandsAProfile()
    {
        // A fraction rather than a fixed index, so a provider offering two models is not held to a four-model rule —
        // and never zero, or a profile with one model could run nothing at all.
        Assert.Equal(2, AutopilotModelTier.AllowedCount(4, AutopilotCostStrategy.Balanced));
        Assert.Equal(2, AutopilotModelTier.AllowedCount(5, AutopilotCostStrategy.Balanced));
        Assert.Equal(1, AutopilotModelTier.AllowedCount(2, AutopilotCostStrategy.Balanced));
        Assert.Equal(1, AutopilotModelTier.AllowedCount(1, AutopilotCostStrategy.Balanced));
        Assert.Equal(1, AutopilotModelTier.AllowedCount(4, AutopilotCostStrategy.CostFirst));
        Assert.Equal(1, AutopilotModelTier.AllowedCount(1, AutopilotCostStrategy.CostFirst));
        Assert.Equal(4, AutopilotModelTier.AllowedCount(4, AutopilotCostStrategy.QualityFirst));
    }

    [Fact]
    public void ValidateAll_ReturnsTheFirstStepOverTheCeiling()
    {
        IReadOnlyList<AutopilotStep> steps =
        [
            Step("haiku"),
            new AutopilotStep("second", "Second", string.Empty, "Claude", "opus", "brief", null),
        ];

        var refusal = AutopilotModelTier.ValidateAll(steps, Roster, AutopilotCostStrategy.Balanced);

        Assert.NotNull(refusal);
        Assert.Contains("second", refusal);
    }

    [Fact]
    public void ValidateAll_WhenEveryStepIsWithinTheCeiling_Passes() =>
        Assert.Null(AutopilotModelTier.ValidateAll([Step("haiku"), Step("sonnet")], Roster, AutopilotCostStrategy.Balanced));
}
