namespace Cockpit.Plugin.Autopilot.Tests;

// The turns the autonomous run hands its sessions (AC-174): a step agent's visible opening instruction (its work plus
// how to report done) and the CEO's validation turn. Kept pure builders off the coordinator so the wording — the tool
// to call, what to include — is tested without a live session.
public class AutopilotStepBriefTests
{
    // The plain coding step every unconditional instruction below is measured against.
    private static AutopilotStep _CodeStep() =>
        new("1", "Code", "d", "Claude", "opus", "do the work", "compiles");

    public static IEnumerable<object[]> OpeningInstructions() =>
    [
        // The work itself, its acceptance, and the tool that reports it finished.
        [
            new[] { "do the work", "compiles", "mcp__cockpit-autopilot-run__autopilot_step_done" },
            Array.Empty<string>(),
        ],
        // The autonomy preamble tells the agent to step past a persona/brain/config prompt instead of waiting for a
        // human — and it names no specific persona, so it stays generic across profiles.
        [
            new[] { "autonomous agent", "persona, brain, or", "do not stop to ask" },
            new[] { "Zyra", "Aura" },
        ],
        // AC-193: a task ambiguity the brief did not spell out is not a mid-run question — the agent makes the most
        // reasonable assumption, follows the codebase's existing conventions, and records it in its done-summary.
        [
            new[]
            {
                "Task ambiguity",
                "most reasonable assumption",
                "FOLLOW THE EXISTING CONVENTIONS",
                "note the assumption in your autopilot_step_done summary",
            },
            Array.Empty<string>(),
        ],
        // AC-201: when a reasonable assumption is not enough, the agent consults its MANAGER (the CEO) via
        // autopilot_blocked — which answers or escalates to the operator — rather than reaching the operator itself.
        [
            new[]
            {
                "Your manager (the CEO) is reachable",
                "autopilot_blocked to consult your manager",
                "escalates to the operator",
                "Never stop for an ordinary judgement call",
            },
            Array.Empty<string>(),
        ],
    ];

    [Theory]
    [MemberData(nameof(OpeningInstructions))]
    public void For_ACodingStep_CarriesItsWorkAndTheAutonomyInstructions(string[] present, string[] absent)
    {
        var brief = AutopilotStepBrief.For(_CodeStep(), agentCount: 1, agentNumber: 1);

        Assert.All(present, phrase => Assert.Contains(phrase, brief));
        Assert.All(absent, phrase => Assert.DoesNotContain(phrase, brief));
    }

    [Fact]
    public void For_FallsBackToTheDescription_WhenNoBriefWasWritten()
    {
        var step = new AutopilotStep("1", "Code", "the description", "Claude", "opus", "  ", "acc");

        Assert.Contains("the description", AutopilotStepBrief.For(step, 1, 1));
    }

    [Fact]
    public void For_ParallelAgent_NamesItsShareOfTheWork()
    {
        var step = new AutopilotStep("1", "Code", "d", "Claude", "opus", "b", "a");

        Assert.Contains("agent 2 of 3", AutopilotStepBrief.For(step, agentCount: 3, agentNumber: 2));
    }

    [Fact]
    public void For_DirectsTheAgentToExecuteAndCommit_NotAnalyseOrPlan()
    {
        // On a local, unbranded profile on purpose: the mandate must read the same there, and the two DoesNotContain
        // assertions below only mean anything when the step itself names neither brand.
        var step = new AutopilotStep("1", "Code", "d", "Qwen (local)", null, "do the work", "compiles");

        var brief = AutopilotStepBrief.For(step, 1, 1);

        // The execution mandate: every model, however light, is told to BUILD — write the code, run
        // the tests, commit in the worktree — and explicitly not to analyse, summarise, ask, or reply with a plan, which
        // is the failure that strands a step on a lighter model.
        Assert.Contains("execution task, not an analysis or planning task", brief);
        Assert.Contains("COMMIT your work in this worktree", brief);
        Assert.Contains("Do NOT instead describe the repository", brief);
        Assert.Contains("verify it builds and its tests pass, commit it, and only then report", brief);
        // Provider-neutral — the mandate names no brand or model.
        Assert.DoesNotContain("Claude", brief);
        Assert.DoesNotContain("opus", brief);
    }

    [Fact]
    public void For_ForAReviewGate_TellsItToReadAndReport_NeverToCommit()
    {
        var step = _CodeStep() with { Title = "Review", IsReviewGate = true };

        var brief = AutopilotStepBrief.For(step, 1, 1);

        // AC-1037: a gate reads a throwaway fork on a branch of its own, so the execution mandate's "COMMIT your work
        // in this worktree" was an instruction to strand its repairs where nothing merges from.
        Assert.DoesNotContain("COMMIT your work in this worktree", brief);
        Assert.Contains("review task, not an execution task", brief);
        Assert.Contains("Do NOT edit the code and do NOT commit anything", brief);
        Assert.Contains("a separate fix step applies them", brief);
    }

    [Fact]
    public void For_StatesDoNotMergeOnlyOnce_NotDuplicatedAcrossTheMandateAndTheFooter()
    {
        var brief = AutopilotStepBrief.For(_CodeStep(), 1, 1);

        // AC-257: the mandate and the footer each used to say "do not merge" — trimmed to the single footer mention.
        var occurrences = brief.ToLowerInvariant().Split("do not merge").Length - 1;
        Assert.Equal(1, occurrences);
    }

    public static IEnumerable<object[]> ValidationTurns() =>
    [
        // The acceptance to judge against, what the agent reported, and the tool that carries the verdict.
        [
            new[] { "opened PR #1" },
            new[] { "compiles", "opened PR #1", "mcp__cockpit-autopilot-ceo__autopilot_validate" },
        ],
        // AC-206: a single whitespace-only summary is treated as no summary — the CEO gets the clear fallback rather
        // than a blank "What the agent(s) reported:" block, like the zero-summary case already does.
        [new[] { "   " }, new[] { "(the agent reported no summary)" }],
        // A parallel step reports once per agent, and every report reaches the CEO.
        [new[] { "did part A", "did part B" }, new[] { "did part A", "did part B" }],
    ];

    [Theory]
    [MemberData(nameof(ValidationTurns))]
    public void ValidationTurn_AsksTheCeoToJudgeAgainstAcceptance_ViaTheTool(string[] summaries, string[] present)
    {
        var turn = AutopilotStepBrief.ValidationTurn(_CodeStep(), summaries);

        Assert.All(present, phrase => Assert.Contains(phrase, turn));
    }
}
