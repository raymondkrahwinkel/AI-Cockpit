---
title: Settings
order: 10
summary: The CEO's profile and model, the cost/safety caps around a run, the executable-stage gate, and the autonomy mode that keeps an isolated step inside its worktree.
icon: 🧭
---

Autopilot's settings do not shape *what* a run does — the CEO decides that per plan, per tracker. They set the
caps and defaults an operator keeps regardless of what the CEO plans: which model plans and validates, how
hard cost weighs against quality, how many times a step may retry, how many runs go at once, which tracker
stage means "ready", and how autonomous a step's CLI session is allowed to be.

## CEO profile and model {#ceo-profile}

**CEO profile** and **CEO model** pick what plans the run. A strong reasoning model is recommended — Autopilot
plans multi-step work up front and needs to reason about the whole shape of it, not just the next edit. Leave
the model blank to use the chosen profile's own default.

**Validation profile** and **validation model** are separate from planning on purpose (AC-254): validation is
the run's high-frequency, growing-context part — every finished step gets checked — so a cheaper model here is
a real cost lever. Leave both blank and validation follows planning's choice; set one and it stops following,
even if the planning pair later changes.

## Cost strategy {#cost-strategy}

**Cost strategy** — cost first, balanced, or quality first — steers where the CEO draws the line between a
local/free model and a paid one for a given step. The CEO always fits the model to the work; this setting only
moves that line, it does not override a step that genuinely needs a stronger model.

## Validator checkpointing {#checkpoint}

A long-running validator re-reads every earlier step's diff on every new turn by default, so a long run pays
for its own history again and again. **Validator starts over every N steps** replaces the validator with a
fresh session that carries forward only a one-line verdict per finished step, instead of the full history.

Setting this to **0** turns checkpointing off entirely — the validator never starts over. That is also the
setting to use if you want to measure a run's behavior without this optimization in the way.

## The autonomy mode, and why bypass is refused {#autonomy-mode}

**Autonomy (permission mode)** controls how much a step's CLI session can do without asking first:
`bypassPermissions` skips permission prompts entirely, `acceptEdits` auto-applies in-worktree edits but still
prompts (and, with no human around, denies) anything outside the worktree, and `default` prompts for
everything.

Autopilot's isolation model depends on a step staying inside its own worktree. For a permission-based provider
(Claude), that confinement *is* the permission system — there is no OS sandbox under it. `bypassPermissions`
disables exactly that guard, and an isolated step's brief can come from an untrusted source (an issue
description, say), which makes this a real prompt-injection path to a write outside the worktree — into the
real checkout, or a dotfile.

Because of that, a stored `bypassPermissions` is silently coerced back to `acceptEdits` for every step type
this plugin runs — planning, implementation, and the review gates alike. If you need genuine autonomous shell
access for a step, run it on an OS-sandboxed provider (Codex) instead, which confines in either mode; picking
`bypassPermissions` for one profile there is a deliberate, per-session choice outside this run-wide setting.

## The executable-stage gate {#executable-stage}

Autopilot refuses to start an item until a person has judged it ready — it does not decide that for itself.
**`<tracker> starts from`** names the stage (or, on a tracker without stages, the label) that means "ready" on
that tracker; an item anywhere else is refused, with the reason posted back to the issue.

Leaving a tracker's field empty starts Autopilot from *any* stage on that tracker — effectively turning the
gate off for it. Autopilot ships a default for YouTrack (`Ready`) and GitHub Issues (the `ready` label); every
other installed tracker needs its stage named explicitly here before Autopilot will pick anything up from it.

## Templates and their placeholders {#templates}

A template is a starting brief for the plan flow. Builtin templates and ones a plugin (YouTrack, GitHub)
registers can be edited — the edit is kept as an override, and **Reset to default** drops it — while your own
templates can also be deleted outright.

A template body can use these placeholders, filled from the triggering issue and any input you supply when
starting a run:

| Placeholder | Fills in |
| --- | --- |
| `{{issue.id}}` | The tracker's issue id, e.g. `AC-513`. |
| `{{issue.title}}` | The issue's title. |
| `{{issue.description}}` | The full description; empty if the tracker gave none. |
| `{{issue.url}}` | A link to the issue. |
| `{{issue.tracker}}` | Which tracker it came from, e.g. `youtrack`. |
| `{{input.<name>}}` | An operator-supplied value by name, e.g. `{{input.branch}}` — only filled for a name you actually ask for. |
