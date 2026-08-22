---
title: Setup
order: 10
summary: Installing the kimi CLI, getting it authenticated, and what silently gets skipped along the way.
icon: 🌙
---

Three things need to be true before this provider works: the `kimi` CLI is installed somewhere this plugin
can find it, it can authenticate (an API key or its own cached login), and — only if you want something other
than Kimi's own default — a model id you have typed correctly, because nothing here validates it for you.

## 1. Install the kimi CLI {#install-cli}

Run `npm i -g @moonshot-ai/kimi-code`. It needs Node ≥ 22.19. This plugin does not install the CLI for you —
see the *Distribution* note in the plugin's `README.md` for why — it only looks for a copy that is already
there: on `PATH`, at a path you pin in the **Kimi command / path** field, or in a cockpit-managed location if
one is ever installed. The settings view resolves the field live as you type and states plainly whether it
found an executable or not.

On Windows, a bare `kimi` resolves to the npm shim (`kimi.cmd`), not `kimi.exe` — this plugin's resolver
probes for that automatically, so pointing the field at plain `kimi` is normally enough.

## 2. Authenticate {#authenticate}

Two independent routes, either is enough:

- **API key** — paste it into the **API key** field. It is stored encrypted at rest by the host and passed
  to the spawned process as the `KIMI_API_KEY` environment variable, never as a command-line argument (which
  would be visible to anything reading the process list).
- **The CLI's own login** — press **Login with Kimi account…**. It runs `kimi acp --login` in a new terminal
  window, because the device-code flow needs a real, interactive terminal that this settings dialog cannot
  provide itself; watch that window for the prompt.

Leaving both empty means every session start fails at the CLI's own auth gate — nothing in this settings view
stops you from saving that configuration.

## 3. Default model (optional) {#default-model}

The **Default model** field is applied once a session exists, best effort: a model id Kimi does not
recognize is skipped rather than failing the session start, so a typo here does not surface as an error —
the session simply runs on whatever Kimi falls back to. Leave it blank to use Kimi's own default.

## What does not reach Kimi at all {#limitations}

Three things are worth knowing before you rely on them, none of them a bug in this plugin:

- **A failed turn looks identical to a successful one.** Kimi reports the same stop reason for both, so
  this provider cannot tell a broken turn from a finished one just from the wire.
- **No quota or cost figures.** Kimi sends no usage data; the context percentage shown in the session header
  is recovered by asking the CLI's own `/usage` command, not from anything Kimi pushes.
- **A system prompt never arrives.** A profile's identity, a project's instructions, or an embedded run's
  briefing are all delivered as a system prompt, and `kimi acp` has no parameter to receive one. Rather than
  drop it silently, the session says so once in the transcript — put anything Kimi must know into your first
  message instead.
