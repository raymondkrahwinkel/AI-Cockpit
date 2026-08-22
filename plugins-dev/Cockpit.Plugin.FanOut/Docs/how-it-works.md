---
title: How fan-out works
order: 10
summary: What an "arm" is, why arms never collide, and what closing the workspace does to all of them.
icon: 🔀
---

A fan-out workspace runs one task on several agents at once so you can read their takes against each other. The
mechanism behind that is simple, but two parts of it are not obvious from the form: how the arms stay out of
each other's way, and what happens when you close the tab.

## What an arm is {#arms}

An arm is one real cockpit session, tiled into this workspace instead of showing up in the session grid. You
set up two to five of them before pressing **Start**; each gets:

- a **profile** — which provider/model runs it, defaulting to the cockpit's own default profile if you leave
  it blank;
- an **angle** — a line appended to the shared task ("Take this angle: …"), telling that arm what to focus on
  or push against.

Vary the profile and leave the angle blank to put several providers on the same brief. Vary the angle and leave
the profile alone to get several takes from one provider. Both columns are always there because it is the same
run either way — you are not choosing a "mode" first.

An arm given no angle at all gets the task completely unchanged. Setting up several arms with the same profile
and no angle is the one way a fan-out cannot pay off: they tend to converge on the same answer, since they were
given nothing to diverge on.

## Why arms never collide {#worktrees}

Every arm's session is started with `IsolateInWorktree: true` against the working directory you gave the
run — each arm works in its own git worktree of that repository, not the one you have open elsewhere. That is
what makes the arms comparable afterwards instead of a race to edit the same files: nothing an arm does can
touch another arm's checkout, or your own.

## Starting is one-shot {#one-shot}

Pressing **Start** replaces the whole setup form with the tile grid and cannot be undone from this workspace —
there is no second **Start** button waiting behind it, and no "add another arm" once running. If you want to
change the task or the arms, close this workspace and open a new one.

## Closing ends every arm, mid-work or not {#closing-ends-everything}

The workspace does not run the arms as a background job you can walk away from and revisit later: closing it
ends every session it holds, immediately, whatever they were doing. There is no confirmation beyond the
window's own close prompt, and no way to detach a single arm to keep running on its own. Read what you need
from a tile — or copy anything worth keeping out of its worktree — before you close the workspace.

## What this workspace does not do {#no-consolidation}

Fan-out does not merge, rank or pick a winner among the arms' work: it only starts them side by side and lets
you read them. Comparing the results and deciding what (if anything) to carry forward is on you, done by hand
against each arm's own worktree.
