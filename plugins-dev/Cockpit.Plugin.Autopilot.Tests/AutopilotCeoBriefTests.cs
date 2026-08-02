using System.Text.RegularExpressions;
using Cockpit.Plugins.Abstractions.Profiles;

namespace Cockpit.Plugin.Autopilot.Tests;

// The CEO planning brief (AC-174): it states the goal, points the CEO at the plan-emit tool, and adapts to whether the
// run was triggered from a source item or started CEO-first. Kept a pure builder off the workspace body so its wording
// is tested without a live session.
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

        Assert.Contains("Ship reading levels in the chat view", brief);
        Assert.Contains("youtrack AC-138", brief);
        Assert.Contains("Reading levels", brief);
        Assert.Contains(AutopilotPlanTools.QualifiedToolName, brief);
    }

    [Fact]
    public void For_ATriggeredRun_TellsTheCeoToReadTheTracker_ButNotToWriteToItWhilePlanning()
    {
        // AC-212 read/write split: while planning the CEO may READ the tracker (open the issue, pull an epic's "parent
        // for" children — AC-217), but must NOT move the issue's stage or post notes yet — that is the run's job (the CEO
        // validator plus the coordinator's auto-advance, AC-202). The write tools autopilot_tracker_stage /
        // autopilot_tracker_note live on the run-only CEO endpoint and must never be named in the planning brief, or the
        // CEO searches for, and reports missing, tools it does not have while planning.
        var plan = new AutopilotPlan(
            "Ship reading levels in the chat view",
            new AutopilotPlanSource("youtrack", "AC-138", "Reading levels"),
            []);

        var brief = AutopilotCeoBrief.For(plan);

        // Reads are invited.
        Assert.Contains("READ the tracker", brief);
        Assert.Contains("child issues", brief);
        Assert.Contains("parent for", brief);
        // Writes are forbidden while planning — the guardrail, not the tool names.
        Assert.Contains("Do NOT move the issue's stage or post notes", brief);
        Assert.DoesNotContain("autopilot_tracker_stage", brief);
        Assert.DoesNotContain("autopilot_tracker_note", brief);
    }

    [Fact]
    public void For_ACeoFirstRun_HasNoTrackerReadOrWriteGuidance()
    {
        // A CEO-first run has no source issue, so neither the read invitation nor the write guardrail belongs — the whole
        // tracker paragraph stays out.
        var plan = AutopilotPlan.Empty(source: null, goal: "Build a feature");

        var brief = AutopilotCeoBrief.For(plan);

        Assert.DoesNotContain("READ the tracker", brief);
        Assert.DoesNotContain("Do NOT move the issue's stage", brief);
    }

    [Fact]
    public void For_ATriggeredRun_SurfacesTheIssueDescription_SoTheCeoDraftsFromWhatItAsks()
    {
        var plan = new AutopilotPlan(
            "Ship reading levels in the chat view",
            new AutopilotPlanSource("youtrack", "AC-138", "Reading levels", "Add Developer/Focus/Simple reading levels to the SDK chat view."),
            []);

        var brief = AutopilotCeoBrief.For(plan);

        Assert.Contains("What the issue asks for", brief);
        Assert.Contains("Add Developer/Focus/Simple reading levels to the SDK chat view.", brief);
    }

    [Fact]
    public void For_ACeoFirstRun_AsksForTheGoalAndCallsItCeoFirst()
    {
        var plan = AutopilotPlan.Empty(source: null, goal: string.Empty);

        var brief = AutopilotCeoBrief.For(plan);

        Assert.Contains("CEO-first", brief);
        Assert.Contains("ask them what this run should achieve", brief);
        Assert.Contains(AutopilotPlanTools.QualifiedToolName, brief);
    }

    [Fact]
    public void QualifiedToolName_CombinesTheEndpointAndToolName()
    {
        Assert.Equal("mcp__cockpit-autopilot-plan__autopilot_plan", AutopilotPlanTools.QualifiedToolName);
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

        Assert.Contains("Qwen (local)", brief);
        Assert.Contains("runs locally, free", brief);
        Assert.Contains("Claude", brief);
        Assert.Contains("hosted API, paid", brief);
        // The suggestions ride along so the CEO knows a profile's model options.
        Assert.Contains("opus, sonnet", brief);
        // The cost-aware selection instruction: default cheap/local, reserve a paid model for steps that need it.
        Assert.Contains("lean cheap", brief);
        Assert.Contains("local, free", brief);
        Assert.Contains("paid, hosted model", brief);
        Assert.Contains("say in the brief why", brief);
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

        // The roster teaches the CEO how to read the signals it has — local-vs-paid, and whatever the provider itself
        // declared about its models. It used to assert an order over the model names on top of that, which was the
        // reverse of the list it described (AC-256); these profiles declare no ranking, so it must claim none.
        Assert.DoesNotContain("lighter/cheaper to heavier/more capable", brief);
        Assert.Contains("in no particular order", brief);
        Assert.Contains("a local profile is usually a lighter model that can stall on a demanding step", brief);
        Assert.Contains("the cheapest option that can actually carry the step to a finished, committed result", brief);
    }

    [Fact]
    public void For_InstructsExecutingStepsGetACapableModel_NotTheLightestJustBecauseItIsFree()
    {
        var plan = AutopilotPlan.Empty(source: null, goal: "Build a feature");

        var brief = AutopilotCeoBrief.For(plan);

        // The execution-fit instruction is unconditional (present even without a roster) and provider-neutral — it steers
        // an EXECUTING step onto a model that can carry it, and off the lightest option chosen merely because it is free.
        Assert.Contains("EXECUTING step", brief);
        Assert.Contains("put an executing coding step on the lightest option merely because it is free", brief);
        Assert.Contains("genuinely do it", brief);
        // Provider-neutral: no brand is prescribed anywhere in the brief.
        Assert.DoesNotContain("Claude", brief);
        Assert.DoesNotContain("qwen", brief);
    }

    [Fact]
    public void For_InstructsTheCeoToWriteClearImperativeSelfSufficientBriefs_ThatNameCommitAndTests()
    {
        var plan = AutopilotPlan.Empty(source: null, goal: "Build a feature");

        var brief = AutopilotCeoBrief.For(plan);

        // The CEO is told to write each step's brief so a light model executes it without interpreting or asking — the
        // second half of the fix (a sharper brief lets a cheaper model succeed).
        Assert.Contains("glass-clear, imperative, fully self-sufficient instruction", brief);
        Assert.Contains("committed in the worktree", brief);
        Assert.Contains("even a light model builds it rather than \"analysing\" it", brief);
        Assert.Contains("cheapest-adequate model reinforce each other", brief);
    }

    [Fact]
    public void For_CostStrategy_TunesTheModelChoiceInstruction()
    {
        var plan = AutopilotPlan.Empty(source: null, goal: "Build a feature");

        Assert.Contains("Cost comes first", AutopilotCeoBrief.For(plan, costStrategy: AutopilotCostStrategy.CostFirst));
        Assert.Contains("Quality comes first", AutopilotCeoBrief.For(plan, costStrategy: AutopilotCostStrategy.QualityFirst));
        Assert.Contains("lean cheap", AutopilotCeoBrief.For(plan, costStrategy: AutopilotCostStrategy.Balanced));
        // The default is Balanced when no strategy is passed.
        Assert.Contains("lean cheap", AutopilotCeoBrief.For(plan));
    }

    [Fact]
    public void For_WithACeoIdentity_TellsTheCeoWhoItIs_AndToKeepTheRunCoherent()
    {
        var plan = AutopilotPlan.Empty(source: null, goal: "Build a feature");

        var brief = AutopilotCeoBrief.For(plan, profiles: null, ceoIdentity: "Zyra (personal)");

        Assert.Contains("Zyra (personal)", brief);
        Assert.Contains("your identity for this run", brief);
    }

    [Fact]
    public void For_TellsTheCeoToSearchDeliberately_ScopeFirst_NotSweepTheWholeRepo()
    {
        var plan = AutopilotPlan.Empty(source: null, goal: "Build a feature");

        var brief = AutopilotCeoBrief.For(plan);

        // AC-197: the CEO is steered to scope-first, targeted search tools and the project graph/index, and away from
        // repeated whole-repo `bash grep -rn` sweeps that burn tokens.
        Assert.Contains("scope first", brief);
        Assert.Contains("Grep, Glob, Read", brief);
        Assert.Contains("graph/index", brief);
        Assert.Contains("bash grep -rn", brief);
    }

    [Fact]
    public void For_WithAnExecutableStage_HoldsAnEpicsChildrenToTheSameBarAsItsParent()
    {
        // The start gate only sees the item the operator clicked; an epic's children are pulled in later, inside this
        // round. Without this the plan quietly executes backlog items on their parent's ticket.
        var plan = AutopilotPlan.Empty(new AutopilotPlanSource("youtrack", "AC-343", "EPIC: Autopilot v2"), goal: "Work the epic");

        var brief = AutopilotCeoBrief.For(plan, executableStage: "Ready");

        Assert.Contains("Take in only the children", brief);
        Assert.Contains("\"Ready\"", brief);
        Assert.Contains("[Brainstorm]", brief);
    }

    [Fact]
    public void For_WithTheGateOff_StillRefusesToFoldInABrainstormChild()
    {
        var plan = AutopilotPlan.Empty(new AutopilotPlanSource("youtrack", "AC-343", "EPIC: Autopilot v2"), goal: "Work the epic");

        var brief = AutopilotCeoBrief.For(plan, executableStage: null);

        Assert.DoesNotContain("Take in only the children", brief);
        Assert.Contains("[Brainstorm]", brief);
    }

    [Fact]
    public void For_SplitsAReviewGatesVerification_NarrowWhileFixing_FullOnTheRoundThatFindsNothing()
    {
        // AC-433: the expensive half is tied to the round that carries the verdict, and only to that one. Each round is
        // asserted together with the scope it owns, in one span — asserting the two round descriptions and the two
        // scopes separately would stay green with the scopes swapped, which is the instruction inverted rather than
        // weakened. The sentence carrying "a narrow round's regression is caught by the full one" is pinned too: that
        // is the ticket's fourth criterion, and it is the reason the cheap half is safe to allow at all.
        var plan = AutopilotPlan.Empty(source: null, goal: "Build a feature");

        var brief = _Unwrapped(AutopilotCeoBrief.For(plan));

        Assert.Contains("only that last round carries the verdict", brief);
        Assert.Contains(
            "A round that ends with findings verifies narrowly: build incrementally and run the tests covering the changed area",
            brief);
        Assert.Contains(
            "The round that ends clean verifies fully: build the whole project from scratch with warnings treated as errors, and run the complete test suite",
            brief);
        Assert.Contains("broke something outside its own test selection is caught exactly there", brief);
    }

    [Fact]
    public void For_AsksEachRoundToReportItsScope_AndToPutTheSameInTheGatesAcceptance()
    {
        // The reporting duty is the half that stops this from being indistinguishable from quietly weakening the gate.
        // What is pinned here is what the brief asks for, not that a report is enforced — nothing in the plugin captures
        // a round's real scope; the acceptance is where it lands so the CEO validator has something to judge against.
        var plan = AutopilotPlan.Empty(source: null, goal: "Build a feature");

        var brief = _Unwrapped(AutopilotCeoBrief.For(plan));

        Assert.Contains("have every round report what it actually built and ran", brief);
        Assert.Contains("in the gate's acceptance", brief);
        Assert.Contains("a gate passes only when its final round verified the whole project", brief);
    }

    [Fact]
    public void For_PutsTheVerificationSplitWithTheGates_Unconditionally_AndNamesNoBuildTool()
    {
        // It belongs to the gate instruction, so it has to read after it rather than float elsewhere in the brief; and
        // like every other instruction here it holds for any project, so it prescribes no build tool.
        var plan = AutopilotPlan.Empty(source: null, goal: "Build a feature");

        var brief = _Unwrapped(AutopilotCeoBrief.For(plan, costStrategy: AutopilotCostStrategy.CostFirst));

        var gates = brief.IndexOf("Standard gates for a run that changes code", StringComparison.Ordinal);
        var verification = brief.IndexOf("Verification cost per review round", StringComparison.Ordinal);
        Assert.True(gates >= 0 && verification > gates, $"gates at {gates}, verification at {verification}");

        Assert.DoesNotContain("dotnet", brief);
        Assert.DoesNotContain("msbuild", brief);
        Assert.DoesNotContain("npm", brief);
        Assert.DoesNotContain("gradle", brief);
    }

    [Fact]
    public void For_WithNoProfilesOrIdentity_OmitsTheRosterAndIdentityLine()
    {
        var plan = AutopilotPlan.Empty(source: null, goal: "Build a feature");

        var brief = AutopilotCeoBrief.For(plan);

        Assert.DoesNotContain("Profiles you can assign steps to", brief);
        Assert.DoesNotContain("your identity for this run", brief);
        // The cost guidance is unconditional — it stands even with no roster passed.
        Assert.Contains("lean cheap", brief);
    }

    // The brief is a wrapped raw string literal, so a sentence in it is broken by a newline and indentation wherever it
    // happened to reach the margin. Collapsing runs of whitespace lets a test assert what the brief says without also
    // pinning how it is laid out — re-wrapping a paragraph is not a behaviour change and must not turn a test red.
    private static string _Unwrapped(string brief) => Regex.Replace(brief, @"\s+", " ");
}
