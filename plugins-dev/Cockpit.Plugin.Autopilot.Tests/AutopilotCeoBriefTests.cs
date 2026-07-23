using Cockpit.Plugins.Abstractions.Profiles;
using FluentAssertions;

namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// The CEO planning brief (AC-174): it states the goal, points the CEO at the plan-emit tool, and adapts to whether the
/// run was triggered from a source item or started CEO-first. Kept a pure builder off the workspace body so its wording
/// is tested without a live session.
/// </summary>
public class AutopilotCeoBriefTests
{
    [Fact]
    public void For_ATriggeredRun_NamesTheSourceItemTheGoalAndThePlanTool()
    {
        var plan = new AutopilotPlan(
            "Ship reading levels in the chat view",
            new AutopilotPlanSource("youtrack", "AC-138", "Reading levels"),
            []);

        var brief = AutopilotCeoBrief.For(plan);

        brief.Should().Contain("Ship reading levels in the chat view");
        brief.Should().Contain("youtrack AC-138");
        brief.Should().Contain("Reading levels");
        brief.Should().Contain(AutopilotPlanTools.QualifiedToolName);
    }

    [Fact]
    public void For_ATriggeredRun_SurfacesTheIssueDescription_SoTheCeoDraftsFromWhatItAsks()
    {
        var plan = new AutopilotPlan(
            "Ship reading levels in the chat view",
            new AutopilotPlanSource("youtrack", "AC-138", "Reading levels", "Add Developer/Focus/Simple reading levels to the SDK chat view."),
            []);

        var brief = AutopilotCeoBrief.For(plan);

        brief.Should().Contain("What the issue asks for");
        brief.Should().Contain("Add Developer/Focus/Simple reading levels to the SDK chat view.");
    }

    [Fact]
    public void For_ACeoFirstRun_AsksForTheGoalAndCallsItCeoFirst()
    {
        var plan = AutopilotPlan.Empty(source: null, goal: string.Empty);

        var brief = AutopilotCeoBrief.For(plan);

        brief.Should().Contain("CEO-first");
        brief.Should().Contain("ask them what this run should achieve");
        brief.Should().Contain(AutopilotPlanTools.QualifiedToolName);
    }

    [Fact]
    public void QualifiedToolName_CombinesTheEndpointAndToolName()
    {
        AutopilotPlanTools.QualifiedToolName.Should().Be("mcp__cockpit-autopilot-plan__autopilot_plan");
    }

    [Fact]
    public void For_WithProfiles_ListsEachWithItsCostNature_AndTellsTheCeoToChooseCostAware()
    {
        var plan = AutopilotPlan.Empty(source: null, goal: "Build a feature");
        var profiles = new[]
        {
            new PluginProfileInfo("Claude", "Plugin", string.Empty) { ModelSuggestions = ["opus", "sonnet"] },
            new PluginProfileInfo("Qwen (local)", "Ollama", string.Empty) { RunsLocally = true },
        };

        var brief = AutopilotCeoBrief.For(plan, profiles);

        brief.Should().Contain("Qwen (local)");
        brief.Should().Contain("runs locally, free");
        brief.Should().Contain("Claude");
        brief.Should().Contain("hosted API, paid");
        // The suggestions ride along so the CEO knows a profile's model options.
        brief.Should().Contain("opus, sonnet");
        // The cost-aware selection instruction: default cheap/local, reserve a paid model for steps that need it.
        brief.Should().Contain("lean cheap");
        brief.Should().Contain("local, free");
        brief.Should().Contain("paid, hosted model");
        brief.Should().Contain("say in the brief why");
    }

    [Fact]
    public void For_WithProfiles_ExplainsALocalProfileMayStall_AndToPickCheapestThatCanCarryTheStep()
    {
        var plan = AutopilotPlan.Empty(source: null, goal: "Build a feature");
        var profiles = new[]
        {
            new PluginProfileInfo("Claude", "Plugin", string.Empty) { ModelSuggestions = ["opus", "sonnet", "haiku"] },
            new PluginProfileInfo("Qwen (local)", "Ollama", string.Empty) { RunsLocally = true },
        };

        var brief = AutopilotCeoBrief.For(plan, profiles);

        // The roster now teaches the CEO how to read the (only) signals that exist — local-vs-paid and the model names —
        // rather than pretending a per-model price tag it does not have.
        brief.Should().Contain("lighter/cheaper to heavier/more capable");
        brief.Should().Contain("a local profile is usually a lighter model that can stall on a demanding step");
        brief.Should().Contain("the cheapest option that can actually carry the step to a finished, committed result");
    }

    [Fact]
    public void For_InstructsExecutingStepsGetACapableModel_NotTheLightestJustBecauseItIsFree()
    {
        var plan = AutopilotPlan.Empty(source: null, goal: "Build a feature");

        var brief = AutopilotCeoBrief.For(plan);

        // The execution-fit instruction is unconditional (present even without a roster) and provider-neutral — it steers
        // an EXECUTING step onto a model that can carry it, and off the lightest option chosen merely because it is free.
        brief.Should().Contain("EXECUTING step");
        brief.Should().Contain("put an executing coding step on the lightest option merely because it is free");
        brief.Should().Contain("genuinely do it");
        // Provider-neutral: no brand is prescribed anywhere in the brief.
        brief.Should().NotContain("Claude");
        brief.Should().NotContain("qwen");
    }

    [Fact]
    public void For_InstructsTheCeoToWriteClearImperativeSelfSufficientBriefs_ThatNameCommitAndTests()
    {
        var plan = AutopilotPlan.Empty(source: null, goal: "Build a feature");

        var brief = AutopilotCeoBrief.For(plan);

        // The CEO is told to write each step's brief so a light model executes it without interpreting or asking — the
        // second half of the fix (a sharper brief lets a cheaper model succeed).
        brief.Should().Contain("glass-clear, imperative, fully self-sufficient instruction");
        brief.Should().Contain("committed in the worktree");
        brief.Should().Contain("even a light model builds it rather than \"analysing\" it");
        brief.Should().Contain("cheapest-adequate model reinforce each other");
    }

    [Fact]
    public void For_CostStrategy_TunesTheModelChoiceInstruction()
    {
        var plan = AutopilotPlan.Empty(source: null, goal: "Build a feature");

        AutopilotCeoBrief.For(plan, costStrategy: AutopilotCostStrategy.CostFirst).Should().Contain("Cost comes first");
        AutopilotCeoBrief.For(plan, costStrategy: AutopilotCostStrategy.QualityFirst).Should().Contain("Quality comes first");
        AutopilotCeoBrief.For(plan, costStrategy: AutopilotCostStrategy.Balanced).Should().Contain("lean cheap");
        // The default is Balanced when no strategy is passed.
        AutopilotCeoBrief.For(plan).Should().Contain("lean cheap");
    }

    [Fact]
    public void For_WithACeoIdentity_TellsTheCeoWhoItIs_AndToKeepTheRunCoherent()
    {
        var plan = AutopilotPlan.Empty(source: null, goal: "Build a feature");

        var brief = AutopilotCeoBrief.For(plan, profiles: null, ceoIdentity: "Zyra (personal)");

        brief.Should().Contain("Zyra (personal)");
        brief.Should().Contain("your identity for this run");
    }

    [Fact]
    public void For_TellsTheCeoToSearchDeliberately_ScopeFirst_NotSweepTheWholeRepo()
    {
        var plan = AutopilotPlan.Empty(source: null, goal: "Build a feature");

        var brief = AutopilotCeoBrief.For(plan);

        // AC-197: the CEO is steered to scope-first, targeted search tools and the project graph/index, and away from
        // repeated whole-repo `bash grep -rn` sweeps that burn tokens.
        brief.Should().Contain("scope first");
        brief.Should().Contain("Grep, Glob, Read");
        brief.Should().Contain("graph/index");
        brief.Should().Contain("bash grep -rn");
    }

    [Fact]
    public void For_WithNoProfilesOrIdentity_OmitsTheRosterAndIdentityLine()
    {
        var plan = AutopilotPlan.Empty(source: null, goal: "Build a feature");

        var brief = AutopilotCeoBrief.For(plan);

        brief.Should().NotContain("Profiles you can assign steps to");
        brief.Should().NotContain("your identity for this run");
        // The cost guidance is unconditional — it stands even with no roster passed.
        brief.Should().Contain("lean cheap");
    }
}
