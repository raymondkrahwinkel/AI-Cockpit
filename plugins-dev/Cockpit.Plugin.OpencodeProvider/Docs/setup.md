---
title: Setup
order: 10
summary: Installing the opencode CLI, when it needs authentication, and the one thing it can never receive.
icon: 🧩
---

Two things need to be true before this provider works: the `opencode` CLI is installed somewhere this plugin
can find it, and — unless you are using one of opencode's own free-tier models — it can authenticate. A
default model is optional and can also be changed later from the session's own model picker.

## 1. Install the opencode CLI {#install-cli}

Follow the installer at [opencode.ai/docs](https://opencode.ai/docs). This plugin does not install the CLI
for you, it only looks for a copy that is already there: on `PATH`, at a path you pin in the **opencode
command / path** field, or in a cockpit-managed location if one is ever installed. The settings view resolves
the field live as you type and states plainly whether it found an executable or not.

The official installer places a real `opencode.exe` (not an npm shim) under `~/.opencode/bin` — on Linux/macOS
that directory, plus `~/.local/bin` and `~/.bun/bin`, is checked automatically even when it is not on the
launching process's own `PATH`, which happens for a GUI or AppImage launch of Cockpit.

## 2. Authenticate — unless you are on a free-tier model {#authenticate}

Most models need one of two independent routes:

- **API key** — paste it into the **API key** field. It is stored encrypted at rest by the host and passed
  to the spawned process as the `OPENCODE_API_KEY` environment variable, never as a command-line argument
  (which would be visible to anything reading the process list).
- **The CLI's own login** — press **Login with opencode account…**. It runs `opencode auth login` in a new
  terminal window, because the login flow needs a real, interactive terminal that this settings dialog cannot
  provide itself; watch that window for the prompt.

opencode's own free-tier models need neither — if that is all you plan to use, this step can be skipped
entirely.

## 3. Choose a model (optional) {#default-model}

The **Default model** field takes opencode's `provider/model` form, e.g. `anthropic/claude-sonnet-4-5` or
`openai/gpt-5.1`. Leave it blank to start on opencode's own default and change it later from the session's own
model picker — nothing here validates the id you type, so a typo is only caught when the session actually
starts.

## What does not reach opencode at all {#limitations}

**A system prompt never arrives.** A profile's identity, a project's instructions, or an embedded run's
briefing are all delivered as a system prompt, and opencode has no parameter over ACP to receive one. Rather
than drop it silently, the session says so once in the transcript — put anything opencode must know into
your first message instead.

Every tool call is still routed through Cockpit's own consent card regardless of what a project's own
`opencode.json` permission settings say — this provider forces its permission policy to always ask while a
Cockpit profile is driving the session, so nothing opencode would otherwise auto-approve skips your review.
