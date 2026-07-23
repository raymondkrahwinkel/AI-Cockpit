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
    /// <summary>
    /// The opening turn a tracker-triggered planning round submits to the CEO (Raymond 2026-07-22): it names the source
    /// issue (tracker, id and title) so the visible prompt says which ticket is being planned — not a generic "the issue
    /// in your brief" — and tells the CEO to draft and emit the plan now. Without any opening turn a system prompt alone
    /// leaves the model idle ("the prompt stays empty"); a CEO-first run has no source and sends no kickoff, so the CEO
    /// asks the operator what the run should achieve instead.
    /// </summary>
    public static string SourceKickoff(AutopilotPlanSource source)
    {
        var title = string.IsNullOrWhiteSpace(source.Title) ? string.Empty : $": \"{source.Title.Trim()}\"";
        return $"Plan {source.Tracker} {source.IssueId}{title}. The issue's details are in your brief — draft the plan "
            + "for it and emit it now with the plan tool, so I can review it. Ask me anything you need to resolve before I approve.";
    }

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
        var executionFit = _ExecutionFit();

        // Read/write split for a source-triggered run (AC-212). While planning the CEO gets the tracker's READ tools —
        // it may open the source issue and, for an epic, pull its "parent for" child issues (AC-217) to plan them as one
        // run — but it must NOT move the issue's stage or post notes yet: nothing has been built and the operator has not
        // approved, so a write now is premature. The write tools (autopilot_tracker_stage / autopilot_tracker_note) are
        // deliberately kept out of the planning scope and belong to the run — the CEO validator (AutopilotValidatorBrief)
        // plus the coordinator's automatic stage-advance (AC-202), both during execution. Provider-neutral: it steers on
        // reading vs writing, never on a specific tracker or tool brand. Omitted for a CEO-first run (no source issue).
        var tracker = plan.Source is { } tracked
            ? $$"""

                This run was triggered from {{tracked.Tracker}} {{tracked.IssueId}}, so you may READ the tracker while you
                plan to inform the plan: open the source issue with the tracker's read tools, and — when it is an epic —
                pull its child issues (its "parent for" / child links) and fold every sub-item into this one plan, rather
                than reading only the description. Do NOT move the issue's stage or post notes on it while planning: nothing
                has been built and the operator has not approved yet, so changing the issue now would be premature. Those
                updates happen during the run, not here — leave the issue where it is until then.

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
            array. When you can resolve the folder the run should work in from the item — the repository the issue is
            about — pass it as workingDirectory too; it pre-fills the operator's field for them to confirm or override (a
            git repository isolates each step in a worktree, a plain folder runs without isolation). Each step: {id,
            title, description, profile, model, brief, acceptance, hard, mcp, agents}.
            - profile: the session profile the step runs on — use one of the exact profile labels listed above. model:
              MUST be exactly one of the models that profile lists above; omit it entirely for a local profile that lists
              no models (it pins its own). A model that is not on the chosen profile's list — or any model on a local
              profile — is rejected and you are asked to fix the plan before the operator can approve it.
            - brief: the context that step's agent is handed. acceptance: what "done" means for the step — you validate
              the step's output against it, and a step that fails goes back to rework within its attempt cap.
            - hard: true for a required gate that must pass (a security review); false or omitted for a skippable step.
            - mcp: the minimal list of MCP server ids the step needs — only what it needs, to save tokens and stay
              least-privilege; leave it empty when the step needs nothing extra.
            - agents: how many agents work the step at once (default 1); more only where the work splits cleanly without
              the parts touching the same files.

            {{costGuidance}}

            {{executionFit}}

            Standard gates for a run that changes code: end the plan with two required (hard) gates, kept as distinct
            steps so a security miss is never lost inside a general pass — a code review (correctness, the actual diff,
            project conventions, tests green) and a separate security review (input handling, secrets, injection, unsafe
            or unsandboxed calls, and the new surface the change exposes), each on a capable model. Include both by
            default; only drop one when the operator says this run does not need it (a docs-only or plainly trivial
            change).

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

            When you need to look at the codebase to plan, search deliberately — scope first. Identify the few directories
            or files that are relevant, then read those with targeted tools (Grep, Glob, Read) and the project's
            graph/index if one is available. Do not run repeated `bash grep -rn` sweeps over the whole repository — that
            burns tokens and time for little signal; reach for a broad scan only after a scoped search has come up empty.
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

    // Two things the CEO must get right for a step to actually get built, independent of the cost strategy above (the
    // live failure this addresses, Raymond 2026-07-23: a demanding coding step put on the lightest local model and handed
    // a vague brief, where the worker "analyses" the repo instead of writing and committing the code, and the step stalls).
    // Provider-neutral on purpose: it steers on capability and cost, never on a brand or a specific model name.
    private static string _ExecutionFit() =>
        """
        Fit the model to the step's real demand, not just its price. An EXECUTING step — one that writes or edits code,
        adds and runs tests, and commits the result — is demanding, multi-step tool work; assign it a model actually
        capable of carrying that through to a finished, committed diff. The lightest, cheapest models (a small local model
        especially) tend to analyse, summarise, or ask a question instead of executing, which stalls the step, so do not
        put an executing coding step on the lightest option merely because it is free — pick the cheapest model that can
        genuinely do it. Keep the lightest and cheapest models for the genuinely trivial steps (a rename, a one-line edit,
        a mechanical change) where they plainly suffice.

        Write each step's brief as a glass-clear, imperative, fully self-sufficient instruction the worker can execute
        without interpreting or asking: spell out exactly what to change, in which files or areas, the concrete end
        result, which tests to add or run, and that the work must be committed in the worktree. Be concrete enough that
        even a light model builds it rather than "analysing" it — a vague or open-ended brief ("look at X", "review the
        repo", "consider whether…") is exactly what makes a worker chat and analyse instead of writing code. A sharper
        brief lets a cheaper model succeed: clear instructions and the cheapest-adequate model reinforce each other.
        """;

    // The profiles the CEO can route steps to, each tagged local-free or hosted-paid, so its model choice is cost-aware.
    // Empty (or none supplied) yields nothing — the brief then leaves the roster out rather than showing an empty header.
    // There is no per-model price/tier field to surface (see PluginProfileInfo: only RunsLocally and the model-alias list),
    // so the roster gives the CEO the two signals that do exist — local-vs-paid and the model names — and tells it how to
    // weigh them, rather than inventing a cost number that is not there.
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
                ? $"; models (lighter/cheaper to heavier/more capable): {string.Join(", ", suggestions)} — a step on this profile must use exactly one of these"
                : "; pins its own model — leave a step's model empty on this profile";
            return $"- {profile.Label} ({cost}{models})";
        });

        return "\nProfiles you can assign steps to (a step's model must be one the profile lists here, or empty for a "
            + "profile that pins its own):\n"
            + string.Join("\n", lines)
            + "\n\nHow to read this roster: \"runs locally, free\" means no API cost, but a local profile is usually a "
            + "lighter model that can stall on a demanding step — it may analyse or summarise instead of executing. "
            + "\"hosted API, paid\" costs money but is generally more capable. Where a profile lists several models, they "
            + "run from lighter and cheaper to heavier and more capable. There is no per-model price tag beyond this "
            + "local-vs-paid signal and the model names, so weigh each step's difficulty against them and pick the "
            + "cheapest option that can actually carry the step to a finished, committed result — not simply the cheapest "
            + "one.\n";
    }
}
