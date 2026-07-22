using Cockpit.Plugins.Abstractions.Profiles;

namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// The briefing the CEO planning session is handed when the planning round opens (AC-174): who it is (its own profile,
/// so it plans in a consistent identity), the goal (and the source item when the run was triggered from one, else that
/// it is a CEO-first run), the profiles it can route work to and what each costs, and how to emit the plan through
/// <see cref="AutopilotPlanTools"/> so the operator sees it and approves it. Kept a pure builder off the workspace body
/// so the exact wording — the tool name, the step shape, the cost guidance — is tested without a live session.
/// </summary>
internal static class AutopilotCeoBrief
{
    /// <summary>
    /// Builds the CEO's planning brief. <paramref name="profiles"/> are the profiles the run can route steps to (the
    /// host supplies them, with each one's local/paid nature) so the CEO can pick the cheapest-adequate model per step;
    /// <paramref name="ceoIdentity"/> is the CEO's own profile label so it plans in one consistent identity. Both are
    /// optional: with none, the brief simply omits the roster and the identity line.
    /// </summary>
    public static string For(AutopilotPlan plan, IReadOnlyList<PluginProfileInfo>? profiles = null, string? ceoIdentity = null, AutopilotCostStrategy costStrategy = AutopilotCostStrategy.Balanced)
    {
        var goal = string.IsNullOrWhiteSpace(plan.Goal)
            ? "The operator has not stated the goal yet — ask them what this run should achieve."
            : plan.Goal;

        var origin = plan.Source is { } item
            ? $"This run was triggered from {item.Tracker} {item.IssueId}: \"{item.Title}\". Draft the plan from it."
              + (string.IsNullOrWhiteSpace(item.Description) ? string.Empty : $"\nWhat the issue asks for:\n{item.Description.Trim()}")
            : "This is a CEO-first run — there is no supplied item; shape the goal and the steps with the operator.";

        var identity = string.IsNullOrWhiteSpace(ceoIdentity)
            ? string.Empty
            : $"\nYou are running as the \"{ceoIdentity}\" profile — that is your identity for this run. The operator chose "
              + "who the CEO is; from here you decide everything per step. Keep the run coherent in that identity, and "
              + "have each step's agent carry it too rather than switching persona mid-run.\n";

        var roster = _Roster(profiles);
        var costGuidance = _CostGuidance(costStrategy);

        // Only a source-triggered run has an issue to keep in sync; a CEO-first run has none, so this stays out.
        var tracker = plan.Source is { } tracked
            ? $$"""

                This run came from {{tracked.Tracker}} {{tracked.IssueId}}, and you are the only one who can keep that
                issue in sync — the step agents cannot touch the tracker. As the run reaches each stage, call
                mcp__{{AutopilotRunTools.EndpointName}}__autopilot_tracker_stage to move the issue (use the tracker's own
                stage names) and mcp__{{AutopilotRunTools.EndpointName}}__autopilot_tracker_note to leave evidence — what
                was done, the result — on it.

                """
            : "\n";

        return $$"""
            You are the CEO of an Autopilot run. In this planning round you build an ordered, executable plan that takes
            the goal to a merge-ready result, and you revise it live as the operator gives you input — this is a
            conversation, so expect them to retarget a step's profile, split work, or drop a gate. Draft your first plan
            from the goal as soon as the operator opens the conversation and emit it right away, so they have something
            concrete to react to.
            {{identity}}
            Goal: {{goal}}
            {{origin}}
            {{roster}}
            Emit the plan by calling {{AutopilotPlanTools.QualifiedToolName}} with a one-sentence goal, a short run name
            (2-5 words) the operator will recognise this run by in the queue and history, and the ordered steps as a JSON
            array. Each step: {id, title, description, profile, model, brief, acceptance, hard, mcp, agents}.
            - profile: the session profile the step runs on (e.g. "Claude", "Qwen (local)"). model: only where the profile
              offers a choice (e.g. "Opus"); omit it for a local profile that pins its own model.
            - brief: the context that step's agent is handed. acceptance: what "done" means for the step — you validate
              the step's output against it, and a step that fails goes back to rework within its attempt cap.
            - hard: true for a required gate that must pass (a security review); false or omitted for a skippable step.
            - mcp: the minimal list of MCP server ids the step needs — only what it needs, to save tokens and stay
              least-privilege; leave it empty when the step needs nothing extra.
            - agents: how many agents work the step at once (default 1); more only where the work splits cleanly without
              the parts touching the same files.

            {{costGuidance}}

            Preflight — resolve every open question now, with the operator, before they approve. Once approved the run is
            autonomous: no human is at the keyboard, and a step's agent that hits an unanswered decision mid-build has to
            stop and ask, which strands the run. So this planning round is your one chance to ask. Surface every ambiguity
            up front — unclear scope or acceptance, a design choice with real trade-offs, a missing name/path/value, which
            of two approaches, anything a worker would otherwise have to interrupt the operator for — and get it decided
            here. Then bake each decision into the relevant step's brief and acceptance so its agent already has the
            answer and never needs to ask. If a genuine unknown cannot be settled in planning, make it its own early step
            that gathers the answer rather than a mid-run question. Aim for a plan a worker can run start to finish without
            once turning back to the operator.

            Two more token savers, whatever the model: give each step only the MCP servers it actually needs (fewer tool
            definitions in its context), and keep each step's brief tight — enough context to do the work, no more.
            {{tracker}}
            Re-emit the whole plan every time you draft or revise it, so the operator always sees the current plan. You do
            not merge or approve anything: the operator approves the plan in the cockpit to start the autonomous run, and
            the final merge stays with them.
            """;
    }

    // The model-choice instruction, tuned to the operator's cost/quality steer (AC-174). All three still fit the model to
    // the work — the strategy only moves where the line between local-free and paid sits.
    private static string _CostGuidance(AutopilotCostStrategy strategy) => strategy switch
    {
        AutopilotCostStrategy.CostFirst =>
            "Cost comes first. Put every step on a local, free model — including work a hosted model would do a little "
            + "better — and escalate a step to a paid model only once a local model has actually failed its acceptance, "
            + "and then to the cheapest paid model that can pass it. Never pick a paid model pre-emptively.",

        AutopilotCostStrategy.QualityFirst =>
            "Quality comes first. Choose the most capable model each step warrants and do not hold a strong model back "
            + "where it improves the result — but this is still not \"everything on the biggest model\": spare an expensive "
            + "model on the plainly trivial work (a mechanical edit, a rename) where a local, free model is obviously "
            + "sufficient.",

        _ =>
            "Model choice is a cost decision — make it deliberately and lean cheap. Default each step to a local, free "
            + "model: a capable local coder handles most coding work (scaffolding, a well-scoped change, mechanical edits, "
            + "refactors, writing tests). Reserve a paid, hosted model for the steps that genuinely need frontier "
            + "reasoning — subtle design or architecture trade-offs, tricky debugging, security-sensitive work, or a step "
            + "a local model keeps failing the acceptance on — and when you pick one, say in the brief why the step needs "
            + "it. Do not put the whole plan on an expensive model \"to be safe\"; that is the waste this avoids.",
    };

    // The profiles the CEO can route steps to, each tagged local-free or hosted-paid, so its model choice is cost-aware.
    // Empty (or none supplied) yields nothing — the brief then leaves the roster out rather than showing an empty header.
    private static string _Roster(IReadOnlyList<PluginProfileInfo>? profiles)
    {
        if (profiles is not { Count: > 0 })
        {
            return string.Empty;
        }

        var lines = profiles.Select(profile =>
        {
            var cost = profile.RunsLocally ? "runs locally, free" : "hosted API, paid";
            var models = profile.ModelSuggestions is { Count: > 0 } suggestions
                ? $"; models: {string.Join(", ", suggestions)}"
                : string.Empty;
            return $"- {profile.Label} ({cost}{models})";
        });

        return $"\nProfiles you can assign steps to:\n{string.Join("\n", lines)}\n";
    }
}
