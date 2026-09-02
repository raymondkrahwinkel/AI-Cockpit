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

    [Theory]
    [MemberData(nameof(WithinTheCeiling))]
    public void Validate_AModelTheCeilingAllows_Passes(object costStrategy, string model, bool reviewGate) =>
        Assert.Null(AutopilotModelTier.Validate(Step(model, reviewGate), Roster, (AutopilotCostStrategy)costStrategy));

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

    [Theory]
    [MemberData(nameof(NothingToJudge))]
    public void Validate_WhatItCannotFairlyJudge_IsLeftAlone(string? model, string profile) =>
        // Under the tightest ceiling there is, so silence here is silence everywhere.
        Assert.Null(AutopilotModelTier.Validate(Step(model, profile: profile), Roster, AutopilotCostStrategy.CostFirst));

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

    [Fact]
    public void HoldToCeiling_LeavesAnythingItCannotJudge()
    {
        IReadOnlyList<PluginProfileInfo> unranked = [new PluginProfileInfo("Codex", "Plugin", string.Empty) { ModelSuggestions = ["dear"] }];

        Assert.Equal("dear", AutopilotModelTier.HoldToCeiling(Step("dear", profile: "Codex"), unranked, AutopilotCostStrategy.CostFirst).Model);
        Assert.Equal("fable", AutopilotModelTier.HoldToCeiling(Step("fable", reviewGate: true), Roster, AutopilotCostStrategy.CostFirst).Model);
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

    [Theory]
    [MemberData(nameof(AllowedCounts))]
    public void AllowedCount_ScalesWithTheRosterAndNeverStrandsAProfile(int rosterSize, object costStrategy, int expected) =>
        Assert.Equal(expected, AutopilotModelTier.AllowedCount(rosterSize, (AutopilotCostStrategy)costStrategy));

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
