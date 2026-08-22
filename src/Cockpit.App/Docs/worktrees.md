---
title: Worktrees
category: system
order: 20
summary: Running a session on its own checkout, and the panel that says what would be lost by removing one.
icon: 🌿
---

Two agents editing one checkout is one agent's work being overwritten by the other's. A git worktree is
git's own answer to that: a second working directory on the same repository, on its own branch, sharing the
history but not the files. Cockpit creates them for you and keeps a register of what it created.

## What isolation actually gives a session {#what-they-are}

Ticking **Isolate in a git worktree** in *+ New session* — or setting it as the default on a
[project](help:core-concepts#project) — gives that session a folder of its own and a branch of its own,
forked from where the source branch is now. The session works there for its whole life; nothing it does
touches the checkout you have open in your editor.

The checkbox is offered only when the folder is a git repository, and says why when it is not. It is a
per-session choice: two sessions on the same repository can be one isolated and one not.

**A worktree is not a copy.** It shares the repository's history, so the branch you make in it is visible
from your ordinary checkout the moment it has commits, and pushing from it pushes to the same remote. What
is separate is the files on disk and which branch is checked out.

**What happens to the source branch.** A session you start yourself brings your checkout along: the source
branch is fast-forwarded first where that is safe, so your work carries forward into the isolated session. A
worktree an agent creates for a subtask of its own never writes to the source checkout — it forks from the
upstream tip instead. Naming a folder is not permission to move somebody's branch.

## Where they live {#where-they-live}

Under the cockpit's own state folder, grouped per repository — never inside the repository being worked on,
so a stray build or a `git clean` in your checkout can never reach one.

The root is a [setting](help:settings#sessions), so you can put them on a faster disk or one with more room.
Changing it affects only worktrees made afterwards: existing ones keep the absolute path they were made at,
so moving the setting never strands what is already there.

Every worktree the cockpit makes is git-locked from creation until it is torn down, so a stray `git worktree
prune` elsewhere cannot pull the ground out from under a live session.

## The worktrees panel {#the-panel}

**Managed worktrees** lists every worktree the cockpit created, with what git reports about it right now.
The register is the source of truth, not the folders: a crash can leave a worktree behind without the
teardown that would have removed it ever running, and this is how the next start finds it again.

Each row carries two labels. The **state** says what would be lost:

| State | What it means |
| --- | --- |
| Clean | Exists, nothing uncommitted, no commit that lives only here. Safe to remove. |
| Uncommitted changes | Work in the folder that is not committed anywhere. |
| *N* commit(s) only here | Committed, but in no other branch and on no remote. |
| Retained | Teardown kept it because it held work. Shown for review, never removed on its own. |
| Folder missing | The folder is gone. Only the register entry is left. |
| No working copy | The folder is there but git does not know it as a working tree any more. |

"Commits only here" is measured against the base branch's *current* tip, and pushed work counts as safe —
so a branch whose commits have since been merged reads as clean instead of showing "3 commits ahead"
forever. It is a question about losing work, not about being up to date.

The **owner** says whether a session still holds the tree — `in use`, naming the pane where it can, or
`session gone`.

### What you can do to a row {#actions}

- **Open folder** — always available, changes nothing. Look at the files yourself.
- **Reattach** — start a fresh session in this worktree. Offered only when the owning session is gone, so
  two sessions never land on one working tree.
- **Release** — detach the tree from the session that claimed it, discarding that session's offer to
  restore into it. No files are touched; the row becomes an ordinary orphan you can remove or start on.
- **Remove** — remove the worktree. Blocked while a session is still on it: close the session first. It
  confirms, and a tree holding unsaved work warns before anything happens.

### Clean up finished {#cleanup}

The **Clean up finished** button sweeps in one go every worktree that has nothing to lose *and* whose
session is gone — the clean ones, and the ones whose folder or working copy is no longer there. It never
touches a tree with uncommitted changes, commits that exist nowhere else, or a live session on it.

Whatever it could not remove is named rather than skipped quietly: a sweep that silently leaves rows behind
reads like a sweep that did nothing.

## Worktrees an agent made for itself {#agent-created}

An agent can create a worktree through the cockpit's own tools to isolate a subtask, and those rows are
listed here beside the rest. They differ in one way: nobody is running *in* one, so the session that asked
for it may remove it while still running. The worktree a session itself runs in stays protected even from
that session, because it is the working directory the session is standing in.

## Where it goes wrong {#troubleshooting}

- **"My changes are not in my editor."** The session is isolated: its work is in the worktree, on its own
  branch. Open the folder from the panel, or merge the branch.
- **A row that will not remove.** Something still holds the folder — a session, an editor, a terminal open
  inside it. Close it and refresh; the panel re-reads git rather than caching what it once saw.
- **A worktree left behind after a crash.** Expected, and the reason the register exists. It shows up as an
  orphan whose session is gone; reattach to it or remove it.
- **Nothing here at all.** No session has been started with isolation on. That is a normal cockpit.
