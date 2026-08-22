---
title: Working with an agent
order: 10
summary: What "coupled" means for a diagram, whiteboard or wireframe, and what an agent can and cannot do to yours.
icon: 🤝
---

A diagram, a whiteboard and a wireframe are three different surfaces, but an agent reaches all three the same
way: through its own MCP tools (`cockpit-diagram`, `cockpit-whiteboard`, `cockpit-wireframe`), gated by the same
shape of consent. This page describes that shared model once, so the per-surface tool descriptions do not have
to repeat it.

## One agent per surface {#one-agent}

A surface — a specific open diagram, whiteboard or wireframe window — can be coupled to at most one agent at a
time. If a second agent asks to use a surface another agent already holds, it is refused outright: "already
being used by another agent." There is no queueing and no takeover; the operator has to close the surface (or
the first agent has to disconnect) before a second one can pick it up. Nothing stops several *different*
surfaces from being coupled to several different agents at once.

## Reading and changing are asked separately {#read-vs-write}

Coupling to a surface grants nothing by itself. An agent asks for read and write (edit / place / draw)
capability separately, and the operator approves each:

- **Read** hands over what is already on the surface — a diagram's full Mermaid source, a wireframe's full
  source, or a screenshot of the whole whiteboard. It never lets the agent change anything.
- **Write** (`edit_diagram`/the per-object diagram tools, `place_on_whiteboard`/`erase_whiteboard_object`,
  `edit_wireframe`) lets the agent change the surface, but only after read has already been granted — asking
  for write when only read is held is shown as a *widening* prompt ("an agent that is reading … now wants to
  …"), not a brand new request.

Approving once covers every later call for that capability on that surface, until the operator disconnects the
agent or closes the window — there is no per-call prompt after that.

## What "changing" is allowed to mean {#change-shape}

The three surfaces do not let an agent change things the same way:

- **Diagram**: an agent can either propose replacing the whole Mermaid source — shown to the operator as a
  block-by-block diff, applied only when they accept it — or make a targeted change to one node, connection,
  entity or attribute (`add_node`, `rename_node`, `connect_nodes`, `set_attribute`, …), which is applied
  straight away without a diff. A targeted edit is refused, not queued, if the operator is editing that same
  node/connection/entity right now.
- **Whiteboard**: an agent only *adds* one object at a time (a shape, a sticky note, a label). There is no
  "replace the board" tool and no way to move, resize or remove anything the operator drew or placed
  themselves. What an agent places is drawn in a distinct blue and badged as the agent's, so it is always
  visible which marks are whose. `erase_whiteboard_object` only ever takes back an object the *same* agent
  placed — asking it to remove one of the operator's own objects is refused, not partially honoured.
  Reading returns a scaled screenshot of the whole board, not its shapes as structured data.
- **Wireframe**: an agent reads and writes the whole plain-text source (see `docs/wireframe-format.md` for the
  format itself) — there is no per-component tool and no diff gate; the operator's own undo history is the
  safety net for an edit they did not want.

## The operator's own opt-out {#consent-toggle}

This plugin's settings page has three independent checkboxes — Skip Diagram / Whiteboard / Wireframe consent —
each off by default. Turning one on lets that surface's open/read/edit(-or-place) tools go straight through
with no Approve/Deny prompt and no line in the consent history for that surface type. It does not change any of
the rules above (still one agent per surface, still add-only on a whiteboard); it only removes the prompts.
