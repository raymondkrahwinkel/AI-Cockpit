namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// The turns the autonomous run hands its sessions (AC-174): a step agent's opening instruction — its brief plus how to
/// report done — and the validation turn the CEO is asked to judge a finished step by. Kept a pure builder off the
/// coordinator so the wording (the tool to call, what to include) is tested without a live session. Unlike the CEO's
/// hidden planning brief (<see cref="AutopilotCeoBrief"/>), the step brief is the agent's <em>visible</em> opening turn —
/// it is the task it was given, submitted for it so an autonomous run needs no human to type the first message.
/// </summary>
internal static class AutopilotStepBrief
{
    public static string For(AutopilotStep step, int agentCount, int agentNumber)
    {
        var work = string.IsNullOrWhiteSpace(step.Brief) ? step.Description : step.Brief;

        var acceptance = string.IsNullOrWhiteSpace(step.Acceptance)
            ? string.Empty
            : $"\n\nAcceptance (what \"done\" means): {step.Acceptance}";

        var parallel = agentCount > 1
            ? $"\n\nYou are agent {agentNumber} of {agentCount} working this step in parallel, each in its own worktree — keep to your part and do not touch what the others own."
            : string.Empty;

        // The agent starts non-interactively under the profile the CEO assigned this step (AC-174, AC-193). No human is
        // here to answer anything this turn, and that cuts two ways. First, a startup question — a project prompt asking
        // which persona/brain/config to load — must be treated as already decided (stay in the identity it launched with)
        // and stepped past, or the run stalls on an unanswered question (the same brain-select trap a spawned sub-agent
        // hits). Second, and the AC-193 fix: a TASK ambiguity the brief did not spell out must not become a mid-run
        // question either — the agent makes the most reasonable assumption in line with the goal and acceptance, follows
        // the codebase's existing conventions (looks at how comparable parts/projects already do it), and carries on,
        // noting the assumption in its done-summary. AC-201: only when it genuinely cannot get there with a reasonable
        // assumption does it consult its MANAGER (the CEO) via autopilot_blocked — the CEO answers or escalates to the
        // operator, so the worker never reaches the operator directly. Kept generic on purpose: it names no specific
        // persona, so it holds whatever the profile is.
        const string autonomy =
            "You are an autonomous agent in an Autopilot run, working under the profile you were launched with — no human "
            + "is available to answer questions this turn. Two things follow. (1) Setup questions: if your startup asks you "
            + "to pick a persona, brain, or configuration before you begin, treat it as already decided, stay in the "
            + "identity you launched with, and go straight to the task below — do not stop to ask. (2) Task ambiguity: for "
            + "anything the brief and acceptance below do not spell out, first try to resolve it yourself — make the most "
            + "reasonable assumption in line with the goal and acceptance, FOLLOW THE EXISTING CONVENTIONS in the codebase "
            + "(look at how comparable parts or projects already do it rather than inventing a new way), and keep going; "
            + "note the assumption in your autopilot_step_done summary. Your manager (the CEO) is reachable when you "
            + "genuinely cannot get there with a reasonable assumption — a real ambiguity, a design call beyond the plan, "
            + "a truly irreversible or destructive choice, or a missing credential: call autopilot_blocked to consult your "
            + "manager, who answers you or escalates to the operator. Never stop for an ordinary judgement call you can "
            + "make yourself.";

        return $$"""
            {{autonomy}}

            {{step.Title}}

            {{work}}{{acceptance}}{{parallel}}

            When the work is complete, call mcp__{{AutopilotRunTools.EndpointName}}__autopilot_step_done with a short
            summary of what you did and the result, so the CEO can validate it against the acceptance. Call it exactly
            once, at the end. Do not merge anything — a human does the final merge.
            """;
    }

    /// <summary>
    /// The one reminder a step agent gets if it goes quiet without reporting done (Raymond 2026-07-22): weaker/local
    /// models sometimes end their turn with a text summary instead of calling the tool, which strands the step. Nudges
    /// the tool call without disrupting an agent that is genuinely still working.
    /// </summary>
    public static string StepDoneReminder() =>
        $"If you have finished this step's work, call mcp__{AutopilotRunTools.EndpointName}__autopilot_step_done now with "
        + "a short summary of what you did — that is how the run advances; a text reply on its own does not report the "
        + "step done. If you are still working, ignore this and call it once you finish.";

    public static string ValidationTurn(AutopilotStep step, IReadOnlyList<string> summaries)
    {
        var reported = summaries.Count <= 1
            ? summaries.Count == 1 ? summaries[0] : "(the agent reported no summary)"
            : string.Join("\n", summaries.Select((summary, index) => $"- Agent {index + 1}: {summary}"));

        var acceptance = string.IsNullOrWhiteSpace(step.Acceptance)
            ? "(no explicit acceptance was set — judge it against the step's intent)"
            : step.Acceptance;

        return $$"""
            A step of the plan has finished — validate it before the run moves on. Step: {{step.Title}}.
            Acceptance: {{acceptance}}

            What the agent(s) reported:
            {{reported}}

            The step's work is in your working directory (the run's worktree, where every step works). Inspect the actual
            files there to check the result against the acceptance — do not rely on the summary alone. Decide whether the
            output meets the acceptance, then call
            mcp__{{AutopilotCeoTools.EndpointName}}__autopilot_validate with passed=true (it meets the acceptance) or
            passed=false (it does not — it will be reworked), and a one-line reason.
            """;
    }
}
