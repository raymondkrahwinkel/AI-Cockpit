---
title: Header indicator
order: 10
summary: The branch and change counts in a session's header, and the one setting behind them.
icon: 🌿
---

This plugin puts a small git indicator in the header of every session, describing the repository that
session is working in — not the cockpit's own checkout, and not whichever repository was opened last.

The dot is the state: clean, changed, or ahead of the remote. Hovering gives the uncommitted and unpushed
counts. Clicking opens that session's uncommitted changes.

The indicator refreshes when the session switches directory and when it runs a git command of its own, so
it follows what the agent is doing rather than a timer.

## Showing the branch name {#branch-name}

By default the header shows only the dot, and the branch name appears on hover. Turning **Show the branch
name in the session header** on puts the name beside the dot permanently.

Worth turning on when sessions in the same window sit on different branches — a worktree run beside the
main checkout, say, where the dot alone tells you the state but not which branch it belongs to. Worth
leaving off when the headers are already crowded: the name is the widest thing in that row.
