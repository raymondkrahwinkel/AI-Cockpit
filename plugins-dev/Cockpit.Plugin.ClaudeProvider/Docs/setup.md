---
title: Setup
order: 10
summary: What has to be true before a Claude profile works — the CLI on this machine, and the two optional overrides.
icon: 🤖
---

Claude ships bundled and pre-approved, so the common case needs nothing filled in here at all: one thing has
to be true outside the app (the `claude` CLI installed and logged in on the machine running Cockpit), and two
fields in this plugin's profile settings are there only for the cases where the default isn't what you want.

## 1. Install and log in to the Claude CLI {#claude-cli}

This plugin drives the real `claude` CLI — both as an interactive TTY session (the actual Claude TUI in a
pane) and headless over its stream-json protocol. Either way, it needs the CLI installed and logged in on the
machine running Cockpit; this plugin has no login flow of its own. Install it yourself, or use the **managed
CLI** panel in this plugin's settings to have Cockpit install and keep a copy for you — once installed, a
profile that doesn't pin an absolute path prefers that managed copy over whatever else is on PATH.

## 2. Config directory (optional) {#config-dir}

Leave **Config directory** blank to use the machine's own `~/.claude` login — the common case. Set it only
when a profile needs to read a different login/config than the machine default, for example to run more than
one Claude account side by side. A path that doesn't exist blocks saving the profile — a typo here would
otherwise silently send the CLI to an empty config and a logged-out session.

## 3. Claude executable / path (optional) {#executable-path}

Leave **Claude executable / path** blank to resolve bare `claude` against PATH (or the managed copy, if
installed). Set it only to pin a specific executable — for example a build outside the managed install, or a
second binary you want a particular profile to use regardless of what else is installed.

## What breaks silently if you skip a step {#silent-breaks}

- **Not logged in on this machine**: the profile looks fine and a session opens, but every turn fails once the
  CLI itself refuses the request — there is no separate "not authenticated" warning before that point, beyond
  the login gate this plugin exposes to the New-session dialog.
- **A config directory that doesn't exist**: the profile refuses to save, rather than silently falling back to
  the default login — the one field here worth getting exactly right.
