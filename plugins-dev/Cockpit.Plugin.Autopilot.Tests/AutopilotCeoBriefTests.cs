using System.Text.RegularExpressions;
using Cockpit.Plugins.Abstractions.Profiles;

namespace Cockpit.Plugin.Autopilot.Tests;

// The CEO planning brief (AC-174): it states the goal, points the CEO at the plan-emit tool, and adapts to whether the
// run was triggered from a source item or started CEO-first. One string from one builder, so "the brief says X" is one
// behaviour with many values: each row is a phrase set it must carry paired with one it must not.
public class AutopilotCeoBriefTests
{
    // Every row below is measured against the same plainest possible plan: CEO-first, no roster, no identity, default
    // cost strategy. That is deliberate — these instructions are unconditional, so the barest brief is where their
    // absence would show first.
    public static IEnumerable<object[]> UnconditionalInstructions() =>
    [
        // The execution-fit instruction: it steers an EXECUTING step onto a model that can carry it and off the
        // lightest option chosen merely because it is free — and prescribes no brand while doing it.
        [
            new[]
            {
                "EXECUTING step",
                "put an executing coding step on the lightest option merely because it is free",
                "genuinely do it",
            },
            new[] { "Claude", "qwen" },
        ],
        // The CEO is told to write each step's brief so a light model executes it without interpreting or asking — the
        // second half of the fix (a sharper brief lets a cheaper model succeed).
        [
            new[]
            {
                "glass-clear, imperative, fully self-sufficient instruction",
                "committed in the worktree",
                "even a light model builds it rather than \"analysing\" it",
                "cheapest-adequate model reinforce each other",
            },
            Array.Empty<string>(),
        ],
        // AC-197: the CEO is steered to scope-first, targeted search tools and the project graph/index, and away from
        // repeated whole-repo `bash grep -rn` sweeps that burn tokens.
        [
            new[] { "scope first", "Grep, Glob, Read", "graph/index", "bash grep -rn" },
            Array.Empty<string>(),
        ],
        // AC-433: each round is asserted with the scope it owns, in one span — asserting descriptions and scopes
        // separately would stay green with the scopes swapped. The "narrow round's regression is caught by the full
        // one" sentence is pinned too: it is the ticket's fourth criterion, the reason the cheap half is safe at all.
        [
            new[]
            {
                "only that last round carries the verdict",
                "A round that ends with findings verifies narrowly: build incrementally and run the tests covering the changed area",
                "The round that ends clean verifies fully: build the whole project from scratch with warnings treated as errors, and run the complete test suite",
                "broke something outside its own test selection is caught exactly there",
            },
            Array.Empty<string>(),
        ],
        // The reporting duty is the half that stops the split from being indistinguishable from quietly weakening the
        // gate. What is pinned is what the brief asks for, not that a report is enforced — nothing in the plugin
        // captures a round's real scope; the acceptance is where it lands so the CEO validator has something to judge.
        [
            new[]
            {
                "have every round report what it actually built and ran",
                "in the gate's acceptance",
                "a gate passes only when its final round verified the whole project",
            },
            Array.Empty<string>(),
        ],
        // With no roster and no identity passed, those two blocks stay out — while the cost guidance, which is
        // unconditional, still stands.
        [
            new[] { "lean cheap" },
            new[] { "Profiles you can assign steps to", "your identity for this run" },
        ],
        // A CEO-first run has no source issue, so neither the tracker read invitation nor the write guardrail belongs —
        // the whole tracker paragraph stays out.
        [
            Array.Empty<string>(),
            new[] { "READ the tracker", "Do NOT move the issue's stage" },
        ],
    ];

    [Theory]
    [MemberData(nameof(UnconditionalInstructions))]
    public void For_TheBarestPlan_CarriesItsUnconditionalInstructions(string[] present, string[] absent)
    {
        var plan = AutopilotPlan.Empty(source: null, goal: "Build a feature");

        var brief = _Unwrapped(AutopilotCeoBrief.For(plan));

        Assert.All(present, phrase => Assert.Contains(phrase, brief));
        Assert.All(absent, phrase => Assert.DoesNotContain(phrase, brief));
    }

    // A tracker-triggered plan. AC-212's read/write split lives here: planning may READ the tracker but must NOT move
    // stage or post notes — that is the run's job (CEO validator + coordinator auto-advance, AC-202). The write tools
    // live on the run-only CEO endpoint, so naming them here makes the CEO report missing tools while planning.
    public static IEnumerable<object[]> TriggeredRunInstructions() =>
    [
        [
            new[] { "Ship reading levels in the chat view", "youtrack AC-138", "Reading levels", AutopilotPlanTools.QualifiedToolName },
            Array.Empty<string>(),
        ],
        [
            new[] { "READ the tracker", "child issues", "parent for", "Do NOT move the issue's stage or post notes" },
            new[] { "autopilot_tracker_stage", "autopilot_tracker_note" },
        ],
        // The issue's own description rides along, so the CEO drafts from what the issue asks for.
        [
            new[] { "What the issue asks for", "Add Developer/Focus/Simple reading levels to the SDK chat view." },
            Array.Empty<string>(),
        ],
    ];

    [Theory]
    [MemberData(nameof(TriggeredRunInstructions))]
    public void For_ATriggeredRun_NamesItsSourceAndTheTrackerReadWriteSplit(string[] present, string[] absent)
    {
        var plan = new AutopilotPlan(
            "Ship reading levels in the chat view",
            new AutopilotPlanSource("youtrack", "AC-138", "Reading levels", "Add Developer/Focus/Simple reading levels to the SDK chat view."),
            []);

        var brief = _Unwrapped(AutopilotCeoBrief.For(plan));

        Assert.All(present, phrase => Assert.Contains(phrase, brief));
        Assert.All(absent, phrase => Assert.DoesNotContain(phrase, brief));
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

    public static IEnumerable<object[]> RosterInstructions() =>
    [
        // Each profile is listed with its cost nature and whatever model options the provider declared, and the
        // cost-aware selection instruction reads off that: default cheap/local, reserve a paid model for the steps
        // that need it, and say why.
        [
            new[]
            {
                "Qwen (local)", "runs locally, free", "Claude", "hosted API, paid", "opus, sonnet",
                "lean cheap", "local, free", "paid, hosted model", "say in the brief why",
            },
            Array.Empty<string>(),
        ],
        // The roster teaches the CEO how to read the signals it has — local-vs-paid, and whatever the provider itself
        // declared about its models. It used to assert an order over the model names on top of that, which was the
        // reverse of the list it described (AC-256); these profiles declare no ranking, so it must claim none.
        [
            new[]
            {
                "in no particular order",
                "a local profile is usually a lighter model that can stall on a demanding step",
                "the cheapest option that can actually carry the step to a finished, committed result",
            },
            new[] { "lighter/cheaper to heavier/more capable" },
        ],
    ];

    [Theory]
    [MemberData(nameof(RosterInstructions))]
    public void For_WithProfiles_ListsThemWithTheirCostNature_AndTeachesHowToChoose(string[] present, string[] absent)
    {
        var plan = AutopilotPlan.Empty(source: null, goal: "Build a feature");
        var profiles = new[]
        {
            new PluginProfileInfo("Claude", "Plugin", string.Empty) { ModelSuggestions = ["opus", "sonnet", "haiku"] },
            new PluginProfileInfo("Qwen (local)", "Ollama", string.Empty) { RunsLocally = true },
        };

        var brief = _Unwrapped(AutopilotCeoBrief.For(plan, profiles));

        Assert.All(present, phrase => Assert.Contains(phrase, brief));
        Assert.All(absent, phrase => Assert.DoesNotContain(phrase, brief));
    }

    // The operator's cost steer, per strategy. The enum is internal, so the rows box it and the test casts it back —
    // the signature stays clean and each case is still discovered and reported under its own enum name.
    public static IEnumerable<object[]> CostStrategies() =>
    [
        [AutopilotCostStrategy.CostFirst, "Cost comes first"],
        [AutopilotCostStrategy.QualityFirst, "Quality comes first"],
        [AutopilotCostStrategy.Balanced, "lean cheap"],
    ];

    [Theory]
    [MemberData(nameof(CostStrategies))]
    public void For_CostStrategy_TunesTheModelChoiceInstruction(object costStrategy, string instruction)
    {
        var plan = AutopilotPlan.Empty(source: null, goal: "Build a feature");

        Assert.Contains(instruction, AutopilotCeoBrief.For(plan, costStrategy: (AutopilotCostStrategy)costStrategy));
    }

    [Fact]
    public void For_WithNoCostStrategy_DefaultsToBalanced()
    {
        var plan = AutopilotPlan.Empty(source: null, goal: "Build a feature");

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

    // The epic gate. The start gate only sees the item the operator clicked; an epic's children are pulled in later,
    // inside this round. Without the stage sentence the plan quietly executes backlog items on their parent's ticket —
    // and with the gate off the brief still refuses to fold in a brainstorm child.
    public static IEnumerable<object[]> EpicGates() =>
    [
        ["Ready", new[] { "Take in only the children", "\"Ready\"", "[Brainstorm]" }, Array.Empty<string>()],
        [null!, new[] { "[Brainstorm]" }, new[] { "Take in only the children" }],
    ];

    [Theory]
    [MemberData(nameof(EpicGates))]
    public void For_AnEpic_HoldsItsChildrenToTheExecutableStage(string? executableStage, string[] present, string[] absent)
    {
        var plan = AutopilotPlan.Empty(new AutopilotPlanSource("youtrack", "AC-343", "EPIC: Autopilot v2"), goal: "Work the epic");

        var brief = AutopilotCeoBrief.For(plan, executableStage: executableStage);

        Assert.All(present, phrase => Assert.Contains(phrase, brief));
        Assert.All(absent, phrase => Assert.DoesNotContain(phrase, brief));
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

    // The brief is a wrapped raw string literal, so a sentence in it is broken by a newline and indentation wherever it
    // happened to reach the margin. Collapsing runs of whitespace lets a test assert what the brief says without also
    // pinning how it is laid out — re-wrapping a paragraph is not a behaviour change and must not turn a test red.
    private static string _Unwrapped(string brief) => Regex.Replace(brief, @"\s+", " ");
}
