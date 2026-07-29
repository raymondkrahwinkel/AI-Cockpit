using System.Globalization;
using Cockpit.Plugins.Abstractions.Profiles;
using Cockpit.Plugins.Abstractions.Sessions;

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
    /// The opening turn a tracker-triggered planning round submits to the CEO: it names the source
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

    public static string For(AutopilotPlan plan, IReadOnlyList<PluginProfileInfo>? profiles = null, string? ceoIdentity = null, AutopilotCostStrategy costStrategy = AutopilotCostStrategy.Balanced, string? executableStage = null)
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

        var roster = _Roster(profiles, costStrategy);
        var costGuidance = _CostGuidance(costStrategy);
        var executionFit = _ExecutionFit();
        var reviewVerification = _ReviewVerification();

        // Read/write split for a source-triggered run (AC-212). While planning the CEO gets the tracker's READ tools —
        // it may open the source issue and, for an epic, pull its "parent for" child issues (AC-217) to plan them as one
        // run — but it must NOT move the issue's stage or post notes yet: nothing has been built and the operator has not
        // approved, so a write now is premature. The write tools (autopilot_tracker_stage / autopilot_tracker_note) are
        // deliberately kept out of the planning scope and belong to the run — the CEO validator (AutopilotValidatorBrief)
        // plus the coordinator's automatic stage-advance (AC-202), both during execution. Provider-neutral: it steers on
        // reading vs writing, never on a specific tracker or tool brand. Omitted for a CEO-first run (no source issue).
        // The start gate (AC-345) only sees the one item the operator clicked. An epic's children come in later, inside
        // this round, so the same bar is stated here for them — the weaker half of the pair, since it is an instruction
        // rather than a check, but the alternative is a plan that quietly executes backlog items on their parent's ticket.
        var childRule = string.IsNullOrWhiteSpace(executableStage)
            ? "Leave out any child still marked [Brainstorm], and say which ones you left out and why."
            : $"Take in only the children a person has already marked ready to be worked on (\"{executableStage.Trim()}\"); "
              + "leave the rest, and any child still marked [Brainstorm], out of the plan and say which ones you left out "
              + "and why. The item the operator clicked passed that check before this round started, and a child pulled in "
              + "behind it meets the same bar rather than riding in on its parent — not on your say-so: pass that child's "
              + "id as the step's issueId and the plan tool checks it against the tracker itself, so a child you got wrong "
              + "is refused rather than quietly executed.";

        var tracker = plan.Source is { } tracked
            ? $$"""

                This run was triggered from {{tracked.Tracker}} {{tracked.IssueId}}, so you may READ the tracker while you
                plan to inform the plan: open the source issue with the tracker's read tools, and — when it is an epic —
                pull its child issues (its "parent for" / child links) and fold every sub-item into this one plan, rather
                than reading only the description. {{childRule}}

                Do NOT move the issue's stage or post notes on it while planning: nothing has been built and the operator
                has not approved yet, so changing the issue now would be premature. Those updates happen during the run,
                not here — leave the issue where it is until then.

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
            title, description, profile, model, brief, acceptance, hard, reviewGate, mcp, agents, issueId}.
            - profile: the session profile the step runs on — use one of the exact profile labels listed above. model:
              MUST be exactly one of the models that profile lists above; omit it entirely for a local profile that lists
              no models (it pins its own). A model that is not on the chosen profile's list — or any model on a local
              profile — is rejected and you are asked to fix the plan before the operator can approve it.
            - brief: the context that step's agent is handed. acceptance: what "done" means for the step — you validate
              the step's output against it, and a step that fails goes back to rework within its attempt cap.
            - hard: true for a required gate that must pass (a security review); false or omitted for a skippable step.
            - reviewGate: true for the code-review/security-review pair (AC-434) — the run reads every step so marked
              concurrently instead of one after another, and treats it as hard regardless of what 'hard' says. Omit for
              every other step.
            - mcp: the minimal list of MCP server ids the step needs — only what it needs, to save tokens and stay
              least-privilege; leave it empty when the step needs nothing extra.
            - agents: how many agents work the step at once (default 1); more only where the work splits cleanly without
              the parts touching the same files.
            - issueId: the tracker item this step is drafted from — the run's own source issue, or (for an epic) one of
              its child issues you folded in — so it can be checked against the tracker itself. Omit for a step with no
              such backing item.

            {{costGuidance}}

            {{executionFit}}

            Standard gates for a run that changes code: end the plan with two required (hard) gates, kept as distinct
            steps so a security miss is never lost inside a general pass — a code review (correctness, the actual diff,
            project conventions, tests green) and a separate security review (input handling, secrets, injection, unsafe
            or unsandboxed calls, and the new surface the change exposes), each on a capable model. Mark both
            reviewGate: true — the run reads them concurrently instead of one after another, and clears whatever either
            finds through one shared fix pass before they re-check (AC-434); you do not plan that fix step yourself, it
            is inserted only when a gate actually finds something. Include both by default; only drop one when the
            operator says this run does not need it (a docs-only or plainly trivial
            change).

            {{reviewVerification}}

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
    // live failure this addresses: a demanding coding step put on the lightest local model and handed
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

    // What each review round is worth verifying (AC-433). A gate reviews, its findings get fixed, and it reviews again
    // until a round finds nothing. The protocol in force during the first pilot asked every one of those rounds for a
    // clean whole-solution build and the entire suite; over two gates that came to eight cycles on one item, of which
    // two carried a verdict. A round that ends in fixes is answering "does the fix work", which an incremental build
    // and the tests around the change already answer. The round that ends clean is what the verdict means, so that one
    // stays whole — three of the four findings in that pilot's second security round were regressions on its own
    // round-1 fixes, which is what a full final round is for. Paired with a reporting duty on purpose: with the scope
    // unreported, a cheaper round and a quietly weakened gate look the same from outside. This is brief text, so it
    // asks rather than enforces — nothing here captures a round's actual scope, which is why the duty is to report it
    // where the validator reads it. Project-neutral like the rest of this brief: it names no build tool.
    private static string _ReviewVerification() =>
        """
        Verification cost per review round. A review gate reviews, its findings get fixed, and it reviews again until a
        round finds nothing — only that last round carries the verdict, so say that in each gate's brief and split the
        cost with it. A round that ends with findings verifies narrowly: build incrementally and run the tests covering
        the changed area, enough to show the fix works and broke nothing around it. The round that ends clean verifies
        fully: build the whole project from scratch with warnings treated as errors, and run the complete test suite.
        Nothing comes off that last round — it is what gives the verdict its meaning, and a fix from an earlier round
        that broke something outside its own test selection is caught exactly there.

        Make that checkable rather than assumed: have every round report what it actually built and ran, and put the
        same in the gate's acceptance, so a gate passes only when its final round verified the whole project and its
        report says so. With the scope unreported, a cheaper round and a quietly weakened gate are indistinguishable.
        """;

    // The profiles the CEO can route steps to, each tagged local-free or hosted-paid, so its model choice is cost-aware.
    // Empty (or none supplied) yields nothing — the brief then leaves the roster out rather than showing an empty header.
    // Beyond local-vs-paid there is now a per-model figure to surface where the provider declared one, and the roster
    // passes it through as the estimate it is; a provider that declared nothing still gets only the two older signals,
    // and the brief says so rather than inventing a number or an order it was never given.
    private static string _Roster(IReadOnlyList<PluginProfileInfo>? profiles, AutopilotCostStrategy strategy)
    {
        if (profiles is not { Count: > 0 })
        {
            return string.Empty;
        }

        var lines = profiles.Select(profile => $"- {profile.Label} ({(profile.RunsLocally ? "runs locally, free" : "hosted API, paid")}{_Models(profile)})");

        return "\nProfiles you can assign steps to (a step's model must be one the profile lists here, or empty for a "
            + "profile that pins its own):\n"
            + string.Join("\n", lines)
            + "\n\nHow to read this roster: \"runs locally, free\" means no API cost, but a local profile is usually a "
            + "lighter model that can stall on a demanding step — it may analyse or summarise instead of executing. "
            + "\"hosted API, paid\" costs money but is generally more capable. Any prices shown are the provider's own "
            + "estimate per million tokens (input/output) and may be out of date — treat them as rough proportions "
            + "between models, never as a quote. A profile listed without prices has not ranked its models at all, so do "
            + "not read any order into it. Weigh each step's difficulty against these signals and pick the cheapest "
            + "option that can actually carry the step to a finished, committed result — not simply the cheapest one.\n"
            + _Ceiling(strategy);
    }

    // Where a provider ranked its own models, the roster follows that ranking and says so; where it did not, the list
    // is explicitly disclaimed as unordered. The previous wording asserted the ranking either way, and was exactly
    // backwards against the list it described (AC-256) — a sentence the CEO obeyed straight into the priciest tier.
    private static string _Models(PluginProfileInfo profile)
    {
        if (profile.ModelCostEstimatesCheapestFirst is { Count: > 0 } ranked)
        {
            return $"; models cheapest first: {string.Join(", ", ranked.Select(_Priced))} — a step on this profile must use exactly one of these";
        }

        return profile.ModelSuggestions is { Count: > 0 } suggestions
            ? $"; models, in no particular order: {string.Join(", ", suggestions)} — a step on this profile must use exactly one of these"
            : "; pins its own model — leave a step's model empty on this profile";
    }

    // Invariant culture on purpose: the brief is English prose read by a model, and a machine set to a comma decimal
    // separator would otherwise render "$1,5" into it.
    private static string _Priced(PluginModelCostEstimate estimate) =>
        estimate is { EstimatedInputUsdPerMillionTokens: { } input, EstimatedOutputUsdPerMillionTokens: { } output }
            ? $"{estimate.Model} (est. ${input.ToString("0.##", CultureInfo.InvariantCulture)}/${output.ToString("0.##", CultureInfo.InvariantCulture)})"
            : estimate.Model;

    // The ceiling is enforced, not requested: a plan that breaks it is refused when you emit it, so you would only
    // find out by being sent back. Saying it here costs one paragraph and saves a round trip.
    private static string _Ceiling(AutopilotCostStrategy strategy) => strategy switch
    {
        AutopilotCostStrategy.QualityFirst =>
            "\nNo cost ceiling applies under this strategy: any model its profile lists is accepted for any step.\n",

        AutopilotCostStrategy.CostFirst =>
            "\nA cost ceiling is enforced when you submit the plan: a step that is not a review gate must use the "
            + "cheapest model its profile lists. Review gates may use any of them — a missed finding costs more than "
            + "the tokens saved. A plan that breaks this is rejected and comes back to you to fix.\n",

        _ =>
            "\nA cost ceiling is enforced when you submit the plan: a step that is not a review gate must use one from "
            + "the cheaper half of its profile's list. Review gates may use any of them — a missed finding costs more "
            + "than the tokens saved. A plan that breaks this is rejected and comes back to you to fix.\n",
    };
}
