---
title: Reviewing changes
order: 10
summary: What the review panel actually shows, and the one step that silently sends your review prompt nowhere.
icon: 🔍
---

Open a session's **Review changes…** action, or click its git badge, and this plugin reads that session's
working directory with `git` and shows what changed — a tree of files on the left, one file's diff on the
right. This page explains what "what changed" means here, and the step that looks like it worked but did not.

## What the diff actually is {#scope}

The panel shows **uncommitted** changes only: `git diff HEAD`, plus every untracked file rendered as an
all-added block so a file the session created shows up too, not just one it edited. It does not show
committed history — a session that committed its own work will show a clean tree here even though the
working directory changed plenty since the review started.

This requires `git` on the machine running Cockpit, and a git repository at the session's working directory.
Missing either one, the panel says so plainly ("No git repository here, or git is not available.") rather
than showing an empty, misleading tree.

A file over 1&nbsp;MB, or one that contains binary data, is listed in the tree with its status but not
drawn — reviewing it line by line was never the point. Each file's diff is also capped at 2000 rendered
lines; past that, use **Copy diff** to get the rest as text.

## Asking the session to review itself {#ask-to-review}

**Ask this session to review** sends a fixed prompt — "review your uncommitted changes, run `/code-review`
over the diff, report findings" — into the session the panel was opened for. It only does this when that
session is the one currently **selected** in the cockpit.

This is the part that is easy to miss: if you opened the review panel for a session and then clicked over to
a different one, pressing **Ask this session to review** does not queue the prompt or redirect it — it shows
a warning toast ("Select this session first…") and sends nothing. Select the session the panel belongs to,
*then* press the button.

## Copying the diff {#copy}

**Copy diff** puts the complete, uncapped diff text (including the untracked-file blocks) on the clipboard —
this is the way to read a file past its 2000-line cap, or to hand the diff to something outside Cockpit
entirely.
