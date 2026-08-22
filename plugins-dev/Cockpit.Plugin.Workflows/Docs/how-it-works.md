---
title: How workflows work
order: 10
summary: The automation model — triggers, data between steps, and why some steps ask the operator first.
icon: ⚙️
---

A workflow is a flow of steps wired together on a canvas: one trigger starts it, and each step after that gets
what the step before it produced. This page covers the parts that are not obvious from the editor itself: what
data a step actually sees, and why some flows can only be built or armed by the operator.

## Armed vs. not {#armed}

A workflow that is **active** (armed) fires by itself the moment its trigger's condition happens — text
appearing in a session, a schedule, an event another plugin contributed. An **inactive** (disarmed) workflow
never fires on its own; it only runs when started by hand, either from the editor's Run button or through the
`run_workflow` MCP tool, and only if it has a manual-start step to run. A flow created through the MCP tools
always starts disarmed — an agent can build one, but arming it is the operator's decision, taken in the editor
or explicitly through `set_workflow_active`.

## What one step hands the next {#data-flow}

A step's parameters can reach the data produced by the step immediately before it, or by name past that:

- `{output}` — a field named `output` (or whatever the previous step produced) from the immediately
  preceding step.
- `{Run a command.output}` — the same field, but reaching back to a specific earlier step by the name shown on
  its canvas node, not just the one right before it.

A step that produces nothing itself passes its input straight through unchanged, so a step later in the chain
can still reach past it to something produced further back.

## Conditions and computed values are JavaScript {#expressions}

Anything written as `{= ... }` is evaluated as a JavaScript expression rather than substituted as text —
`{= output.split('\n').length}`, `{= exitCode != '0'}` — and a decision step's condition (`If`, `Switch`) is
nothing but one such expression. Every field of the incoming data is available as a bare variable (`output`),
and `step('Step name')` reaches an earlier step's data the same way `{Step name.field}` does in plain
substitution.

This is a real script engine (Jint), not a small custom formula language, so it can do arithmetic, string
methods and boolean logic — but it is not a sandbox to be trusted with untrusted input: a workflow that runs a
shell command already runs with the operator's rights, and an expression is capped at one second and 10,000
statements purely to stop a runaway loop from hanging the run, not to contain anything hostile. An expression
that cannot be evaluated fails the step outright rather than silently counting as empty or false.

## Every step declares how dangerous it is {#consent-tiers}

Every non-trigger step — built-in or contributed by another plugin through `ICockpitHost.AddWorkflowStep` —
declares one of three consent tiers:

- **None** — runs with no prompt at all (a notification, a status update).
- **LowRisk** — runs with no prompt, but is logged.
- **Dangerous** — runs with the operator's own rights (a shell command, starting a session, calling an
  external service), and asks for Approve/Deny the first time it runs in a given flow.

A step that does not declare a tier is left out of the flow entirely with a warning naming it and the plugin
that shipped it — a workflow never runs a step ungated by falling through a gap in its own author's
declaration.

This tiering is also what an agent cannot do over the MCP tools: `create_workflow`, `update_workflow` and
`set_workflow_active` all refuse outright if the flow contains a Dangerous step, naming it and pointing at the
operator. A flow that already exists with a Dangerous step in it can still be *run* by an agent
(`run_workflow`) — that just asks the operator to Approve that step the same way running it by hand would.

## What a failed step means for the run {#failure}

A step this build cannot execute — an unrecognised type, a step whose plugin is not installed — is passed by
with a reason recorded against it rather than silently counted as having succeeded. The run's history keeps
every step's outcome, so "it did not work" is something you can act on instead of a flow that just quietly
produced nothing.
