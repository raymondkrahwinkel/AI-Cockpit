# AI-Cockpit

[![License: AGPL v3](https://img.shields.io/badge/License-AGPL_v3-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/)
[![Avalonia](https://img.shields.io/badge/Avalonia-12-8B44AC.svg)](https://avaloniaui.net/)

A **desktop cockpit for running multiple AI coding/chat sessions side by side** — one window, N
independent sessions in a grid. The primary backend is **Claude Code** (each session a headless
`claude` process), and profiles can also point at **local models** (Ollama, LM Studio); each session
has its own profile, and — for Claude — permission gating, model and thinking-effort controls, plus a
readable chat transcript.

- **Multi-session grid** — run several Claude Code sessions at once; a sidebar shows each session's
  live status (busy / waiting / needs attention / done), with zoom, close affordances and a
  keyboard session switcher (Ctrl+Arrow, configurable).
- **Real permission gating** — the cockpit hosts an in-process MCP permission-prompt server, so
  every tool call Claude wants to make pauses for an explicit **Allow / Deny / Always** decision in
  the UI. "Always" rules persist per profile (exact or wildcard scope).
- **Profiles** — point sessions at different `CLAUDE_CONFIG_DIR`s (e.g. work vs. personal), each
  with its own login, defaults (permission mode / model / effort) and executable.
- **Readable transcript** — assistant responses render as markdown (headings, code blocks with
  syntax colouring, lists), tool calls collapse to compact headers with their results coupled
  underneath, JSON results get a copy button, emoji render properly, and text is selectable.
- **Live controls** — switch model and thinking-effort budget mid-session over the CLI's control
  channel; interrupt a running turn with Stop.
- **Multi-provider (local models)** — a profile can run **Ollama** or **LM Studio** instead of
  Claude, over their OpenAI-compatible `/v1` endpoint. Pick the provider when you create the profile
  (fixed afterwards), set its base URL and model (Fetch lists the installed models), and give it an
  optional **base system prompt**. The UI adapts to what a provider supports — a chat-only local
  session simply doesn't show the Claude-only permission/effort controls (no dead controls). *Tool
  use / MCP for local models is in progress.*
- **Voice** — hands-free dictation and talk-back: hold **push-to-talk** (Whisper STT + Silero VAD,
  with an optional local-LLM cleanup pass) or flip on **open-mic** to listen continuously with
  automatic start/stop detection, and have replies **read aloud** (Piper voices via sherpa-onnx) with
  Dutch/English voice routing. All opt-in and fully local.
- **Plugins** — extend the cockpit with your own settings, sidebar sections and dialogs, and let a
  plugin act on the active session — installed from a zip or a plugin store, with a first-load
  consent that pins the assembly hash. See the [plugin docs](#documentation).
- **Extras** — paste images from the clipboard straight into the conversation, presence-aware
  notifications (OS toast when you're at the machine, Discord webhook when you're away), and an
  experimental raw-TTY mode (Windows) that embeds the real `claude` terminal UI.

> **Status: pre-1.0, in active development.** Expect breaking changes between commits; there are no
> releases yet. Built and tested on Linux (Fedora) and Windows.

---

## How it works

The cockpit is a native [Avalonia](https://avaloniaui.net/) app (C#/.NET 10) that drives the
**official Claude Code CLI**: each session is a headless `claude` child process speaking
`stream-json` over stdin/stdout. Nothing talks to the Anthropic API directly — authentication is
whatever your normal `claude /login` produced, and the cockpit only ever *checks that a login
exists*; it never reads, stores or transmits credentials or API keys.

Permission prompts work through the CLI's own extension point: the cockpit registers itself as an
MCP `--permission-prompt-tool`, so Claude asks the cockpit before running a tool, exactly like the
interactive CLI would ask in the terminal.

A **local-model** profile uses a different driver: it talks to a locally running Ollama or LM Studio
server over its OpenAI-compatible `/v1` endpoint (via `Microsoft.Extensions.AI`), streaming replies
into the same transcript. Both driver kinds sit behind one `ISessionDriver` seam and advertise their
`SessionCapabilities`, so the UI renders per provider. Local models stay on your machine; nothing is
sent to a third party.

## Requirements

| Tool | Version |
|------|---------|
| .NET SDK | **10.0+** |
| Claude Code CLI (`claude`) | installed and logged in (`claude /login`) with an active subscription |

## Running

```
dotnet run --project src/Cockpit.App
```

Create a session via **+ New session**: pick a profile, permission mode, model and effort — the
session starts immediately. Settings live in `cockpit.json` next to the app's config
(notifications, session-switch shortcut, per-profile defaults and always-allow rules).

For UI development there is a headless verification mode that renders the main window off-screen
and writes a PNG (no display needed):

```
dotnet run --project src/Cockpit.App -- --screenshot out.png
```

Tests:

```
dotnet test
```

## Documentation

- **[Plugin SDK guide](docs/plugins/PLUGIN-SDK.md)** — build a plugin that extends the cockpit
  (settings, sidebar, dialogs, session actions), package it, install it, and publish a plugin store.
  A complete working example lives in
  [`plugins-dev/Cockpit.Plugin.GitHubIssues`](plugins-dev/Cockpit.Plugin.GitHubIssues).
- **[Plugin API reference](docs/plugins/API-REFERENCE.md)** — every method a plugin can call
  (`ICockpitHost`, `ICockpitActions`, `IPluginStorage`, `IPluginSettingsView`), with signatures,
  parameters and short examples.
- **Scaffold a new plugin:** `dotnet new install ./templates/cockpit-plugin` then
  `dotnet new cockpit-plugin -n My.Plugin -o plugins-dev/My.Plugin`.

## How this project is built

This project is developed in an **AI-assisted workflow** — fittingly, since it is itself a tool for
working with AI agents. The direction, architecture decisions, feature choices and acceptance
testing are human; the majority of the implementation is written by AI agents (Anthropic's Claude)
orchestrated through Claude Code by the maintainer.

That workflow does not lower the bar — it *is* the bar this tool exists to support:

- every change is reviewed, must build with **zero warnings**, and ships with unit tests
  (xUnit; several hundred and counting);
- UI changes are verified visually against rendered screenshots before they land;
- features that touch the real CLI are verified end-to-end against a live `claude` process, not
  assumed from documentation;
- the maintainer signs off on every commit and takes full responsibility for the result.

If you find something that looks generated-and-unread, that is a bug in the process — please open
an issue.

## Disclaimer

AI-Cockpit is an independent open-source project. It is **not affiliated with, endorsed by, or
sponsored by Anthropic**. "Claude" and "Claude Code" are trademarks of Anthropic, PBC.

The cockpit launches the official Claude Code CLI under **your own login and subscription**; your
use of Claude through this tool is governed by your own agreement with Anthropic (Terms of
Service / Usage Policy). The tool is **single-user by design**: it is a local desktop app for
driving your own sessions, not a service, proxy or account-sharing layer.

## Support

- **Found a bug or have a feature request?** [Open an issue](https://github.com/raymondkrahwinkel/AI-Cockpit/issues).
- **Security issue?** Don't open a public issue — follow [`SECURITY.md`](SECURITY.md) to report it privately.

## Contributing

Contributions are welcome, but the bar is high for a one-person project. **Read
[`CONTRIBUTING.md`](CONTRIBUTING.md) before opening a pull request.** By participating you agree to
the [Code of Conduct](CODE_OF_CONDUCT.md).

## Licence

AI-Cockpit is licensed under the **GNU Affero General Public License v3.0** — see
[`LICENSE`](LICENSE).
