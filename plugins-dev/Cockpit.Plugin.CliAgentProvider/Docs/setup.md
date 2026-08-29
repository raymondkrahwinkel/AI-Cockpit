---
title: Setup
order: 10
summary: Installing and logging in to the Codex CLI, and the sandbox/working-directory settings that change what it can touch.
icon: 🛠️
---

Four things need to be true before a Codex profile works: the `codex` command has to resolve to a real
executable, you have to be logged in (or have an API key ready), the sandbox mode has to match what you want
Codex to be allowed to touch, and — for a headless (SDK) session specifically — the working directory has to
be set. This page walks through all four.

## 1. Make the command resolve {#codex-command}

The **Codex command / path** field defaults to bare `codex`, which is resolved against PATH — including a
Windows `.cmd` npm shim, if that's how you installed it. If you'd rather not rely on PATH, paste an absolute
path to the executable instead, or let Cockpit install and manage a copy for you from the panel below the
field (**Managed by Cockpit**): once installed, the plugin prefers that copy over anything on PATH unless you
pinned an absolute path yourself.

The status line under the field tells you, right away, which of the three it resolved to and whether it
found anything at all — a profile with a command that does not resolve fails the moment a session tries to
spawn it, not when you save the profile.

## 2. Log in, or set an API key {#api-key}

Codex needs to be authenticated on the machine running Cockpit. The normal way is running `codex login`
yourself in a terminal, once, outside of Cockpit — this plugin has no login flow of its own for the API-key
route.

The **API key (optional)** field is only needed if this machine is not already logged in that way: filling it
in sets `CODEX_API_KEY` for that spawn only — it is never passed as a CLI argument and never logged. Leave it
blank on a machine that already has `codex login` done.

## 3. Pick a sandbox mode {#sandbox-mode}

**Sandbox** is set under **Session defaults**, further down the profile, alongside Model, Effort and Approval —
not in this panel. Leaving it unset is a real choice: the profile then starts on whatever sandbox it already
carried, which for a profile made before Session defaults existed is the one it has always run on.

Sandbox controls what Codex is allowed to touch once it's running:

- `read-only` — Codex's safe default; no edits at all.
- `workspace-write` — allows edits inside the working directory.
- `danger-full-access` — runs Codex with no sandboxing whatsoever. Only use this on a machine or working
  directory you fully trust — nothing in this plugin's headless route can intervene once you have.

This matters more here than for a TTY session: an SDK (headless) session has no in-band tool-permission
channel at all — Codex sets its own approval policy to "never" in headless mode — so whatever the sandbox
mode allows, it allows silently, with no prompt to catch a mistake.

## 4. Set a working directory, for SDK sessions only {#working-directory}

A **TTY** session (the interactive Codex TUI in a pane) runs wherever the New-session dialog says, so it
ignores this field entirely. An **SDK** (headless) session is different: the plugin contract this driver is
built on carries no working directory at all, so the driver has nowhere else to learn one from — it reads
this field instead, and falls back to the cockpit's own directory when the field is empty. This is also the
sandbox root: with `workspace-write`, edits are confined to whatever directory ends up used here.

## What breaks silently if you skip a step {#silent-breaks}

- **Not logged in and no API key**: the profile looks fine and the session opens, but every turn fails once
  Codex itself refuses the request — there is no separate "not authenticated" warning from this plugin.
- **Wrong sandbox mode for an SDK session**: because headless Codex takes no approval prompts, a
  `workspace-write` or `danger-full-access` profile edits files the moment it decides to, with nothing in this
  plugin to ask first.
- **Working directory left unset for an SDK session**: it silently falls back to the cockpit's own directory
  rather than failing — worth checking if edits are landing somewhere unexpected.
