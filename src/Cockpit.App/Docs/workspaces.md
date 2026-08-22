---
title: Workspaces
category: system
order: 30
summary: The tabs above the grid — what each kind holds, how they lay themselves out, and why agents call one a desk.
icon: 🗂️
---

A workspace is a named, remembered arrangement of panes, switched from the strip above the grid. It is the
cockpit's answer to "I am doing two unrelated things today": one workspace per thing, each keeping its own
sessions, its own layout and its own place on screen.

The strip hides itself while there is only one workspace — that is the cockpit as it has always looked — but
the **+** stays, since it is what makes the second one.

## The kinds, and why the kind is asked first {#kinds}

The **+** asks what the workspace is for before it makes it, and the answer is fixed afterwards.

| Kind | Holds | Its **+** offers |
| --- | --- | --- |
| **Sessions** | [Sessions](help:core-concepts#session) and plain terminals — the working surface. | A new session, or a terminal. |
| **Dashboard** | Widgets from installed [plugins](help:core-concepts#plugin) — the glanceable surface. | Add widget. |
| **Projects** | The [projects](help:core-concepts#project) overview, as cards each one Start away. | Add a project. |
| A plugin's own | Whatever that plugin's workspace type draws. | Whatever it registered. |

Asking up front is what keeps the surface honest: the kind decides which `+` you get, which empty state you
see, and what may live in the workspace at all — so widgets never land in a grid of sessions, and no control
on screen is one that would do nothing if pressed.

A plugin can add a kind of its own. If you open a cockpit whose config names a kind no installed plugin
provides, that workspace shows a placeholder saying so instead of disappearing.

## Panes {#panes}

Inside a Sessions workspace, everything is a pane in the same draggable grid: an AI session, a plain
terminal, a widget on a dashboard. The grid places rectangles and does not care which is which — but the
kind of pane decides which chrome it gets, so a terminal never shows a permission-mode dropdown that would
mean nothing to it.

Panes are remembered. A restart brings back the panes that were there, in the places they were in; a session
pane comes back without starting itself, and offers to pick up the conversation it was holding.

## How a workspace lays itself out {#layout}

The **Layout** button above the grid decides how *this* workspace arranges its sessions: one session at a
time, stacked in a single column, focus plus a mini-rail of the rest, or the free grid.

It opens on **Use the default layout (Options)**. Switch that off and the workspace arranges itself from
then on — and the [Appearance page](help:settings#appearance) no longer moves it. That is the whole point:
the workspace you keep two sessions side by side in should not have to agree with the one holding eight.

A Dashboard workspace has its own ⚙ instead, for the grid its widgets snap to: columns, rows, and whether
the cell lines are drawn while you arrange. Rows are a starting height, not a ceiling — the dashboard grows
past it as widgets are added. A dashboard can also be exported to a file and imported again, which is how
one is handed to someone else; credentials are left out of the export.

## Getting around {#switching}

`Ctrl+Shift+Left` and `Ctrl+Shift+Right` step through the workspaces, wrapping at the ends. They are the
horizontal pair on purpose: `Ctrl+Shift+Up`/`Down` step the session list, which is a vertical strip, and the
arrows match what they move through on screen. Both keep working while a terminal has focus, since that is
where you switch from.

Double-click a tab to rename it, drag it to reorder, and right-click for rename and close. Names may repeat
— a workspace is found by its own id, so two called "scratch" are not a problem.

## A workspace is also a desk {#desks}

The agents running in a workspace can see each other. That is what the cockpit's own agent tools mean by a
**desk**: the sessions on one workspace are neighbours, and can list one another, claim a branch or a
worktree so nobody else takes it, and send each other messages. Sessions on another workspace neither see
those claims nor block them.

So which workspace a session runs in is a real decision, not only a visual one. Two agents that should
coordinate belong on one desk; two that should stay out of each other's way belong on separate ones.

## Where it goes wrong {#troubleshooting}

- **A workspace whose body is a placeholder.** Its kind came from a plugin that is not installed here. The
  workspace and its panes are kept, not discarded — install the plugin and it renders again.
- **Options no longer changes a workspace's layout.** That workspace has been given a layout of its own.
  Turn **Use the default layout** back on in its Layout button.
- **Closing a workspace closes what is on it.** It asks first, and says how many sessions it is about to
  stop — counting the ones that are actually running apart from the ones restored but never started. It
  cannot be undone.
