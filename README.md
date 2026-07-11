# AI-Cockpit

[![License: AGPL v3](https://img.shields.io/badge/License-AGPL_v3-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/)
[![Avalonia](https://img.shields.io/badge/Avalonia-12-8B44AC.svg)](https://avaloniaui.net/)

A desktop cockpit (Avalonia, .NET 10) for running **multiple AI coding/chat sessions side by side** — one
window, N independent sessions in a grid. Each session runs a headless `claude` CLI process by default, or
one of several other providers; each has its own profile, permission gating, model/effort controls, and a
readable transcript.

> **Status: pre-1.0, in active development.** Expect breaking changes between commits; there are no releases
> yet. Built and tested on Windows and Linux (Fedora).

## How it works

The cockpit drives the **official Claude Code CLI**: an SDK-mode session is a headless `claude` child process
speaking `stream-json` over stdin/stdout. Nothing talks to the Anthropic API directly — authentication is
whatever your normal `claude /login` produced; the cockpit only ever checks that a login exists and never
reads, stores or transmits credentials or API keys. Permission prompts work through the CLI's own extension
point: the cockpit registers itself as an MCP `--permission-prompt-tool`, so Claude asks the cockpit before
running a tool, exactly like the interactive CLI would ask in a terminal.

A local-model profile (Ollama/LM Studio) or a provider-plugin profile uses a different driver instead, but
every driver sits behind one internal session-driver seam and advertises its own capabilities, so the UI
renders only the controls a given provider actually supports.

## Session modes: SDK and TTY

Two ways to run a Claude session, picked per session in **+ New session** (defaults to TTY; forced to SDK for
non-Claude providers, since only the Claude CLI has an interactive TUI):

- **SDK** — the default headless mode: `claude` runs with `-p`/`stream-json`, the cockpit parses events and
  renders its own chat transcript (markdown, collapsible tool calls, copyable JSON results, live model/effort
  switch, Stop-to-interrupt).
- **TTY** — the *real* interactive `claude` terminal UI, embedded via a pseudo-console (ConPTY on Windows,
  `Porta.Pty` cross-platform) and rendered with an adjustable font (family + size). No stream-json parsing —
  you get the actual CLI experience inside a cockpit pane.

## Multi-provider

A profile's provider is fixed at creation:

- **Built-in:** Claude CLI (the default), **Ollama**, **LM Studio** — the latter two over their
  OpenAI-compatible `/v1` endpoint (base URL + model, model list fetch, optional base system prompt).
- **Provider plugins** add further choices to the same picker, each backed by a plugin-supplied driver:
  - **Gemini / OpenAI Provider** — Gemini and OpenAI, both over an OpenAI-compatible chat-completions
    endpoint.
  - **GitHub Models** — GitHub's own Models API (`models.github.ai/inference`) with a PAT.
  - **CLI Agent Provider** — Codex CLI, driven as a subprocess per turn (`codex exec --json`, resumed for
    follow-up turns).
- Every provider (built-in or plugin) advertises capabilities (tools, permissions, live model switch, ...) so
  the session UI only shows controls it can actually back.

See the [plugin docs](#documentation) to add your own provider.

## MCP

- A **shared MCP-server registry** (Options → MCP servers) holds every configured server, each scoped to
  **All** sessions, **local-only** (Ollama/LM Studio), or **Claude-only**.
- **Per-session selection** — checkboxes in the New-session dialog pick which registered servers a given
  session actually gets.
- Two fan-out paths consume the registry: the **Claude fan-out** (serialized into `--mcp-config`/
  `--strict-mcp-config`, alongside the cockpit's own permission-prompt server) for Claude CLI sessions, and a
  **local tool-loop** (a direct MCP client, stdio/HTTP) for local-model sessions.
- An **approval gate** wraps every tool the local tool-loop exposes, so a local-model session asks Allow/Deny
  through the same UI Claude's permission prompts use.
- **YouTrack MCP** — the YouTrack plugin registers each configured instance's JetBrains remote MCP endpoint
  into the shared registry automatically, so a session gets YouTrack tools with no manual server setup.

## Voice

- **STT (dictation):** Whisper.net transcription with automatic backend selection — tries CUDA, then
  CUDA12, then Vulkan (Windows only), then CPU, honoring an explicit override (`Auto`/`Cuda`/`Vulkan`/`Cpu`).
- **Open-mic mode:** continuous listening with Silero VAD-based endpointing (configurable silence timeout) —
  no push-to-talk needed.
- **Push-to-talk:** a configurable key (default `F9`); optionally a **global hotkey** that works even when
  the cockpit isn't focused (a low-level keyboard hook on Windows, the XDG global-shortcuts portal on
  Wayland/Linux), paired with a small **desktop overlay** window showing listening/transcribing state and a
  live waveform.
- **TTS (read-aloud):** replies can be read aloud via sherpa-onnx running Piper voices, with separate
  Dutch/English voices and automatic language routing within a single reply (an optional naturalization pass
  marks language-switch points, which are split into per-language segments and routed to the matching voice).
- All voice features are **opt-in and fully local** — no audio leaves the machine.

## Plugins & plugin store

- Plugins are .NET assemblies loaded from `plugins/` under the config directory, each implementing one
  interface and contributing settings, sidebar buttons/sections, dialogs, session providers, and/or MCP
  servers through a host facade. See the [plugin docs](#documentation) for the full API.
- **Install from zip:** Options → Plugins → Install from zip…, then **Review & enable**. First load requires
  explicit **consent** (name/version/author/path/hash shown, "runs unsandboxed" warning); the entry
  assembly's **SHA-256** is pinned, so a later tampering or update re-prompts for consent.
- **Plugin store dialog:** categories sidebar, searchable/sortable cards, a detail panel (description,
  author, links, version history) — reuses the exact same download → SHA-256-check → consent → enable
  pipeline as a manual zip install. A **default store** is pre-seeded on first run
  (`github.com/raymondkrahwinkel/AI-Cockpit-Plugins`); add others under Options → Plugins → Plugin stores.
- A periodic **update check** compares installed plugin versions against each configured store's catalogue
  and raises a toast when a newer version is available, linking straight into the store dialog's
  Updates-available filter.
- Enabling/disabling/installing a plugin needs a **restart** to take effect (a loaded assembly can't be
  unloaded); a "Restart cockpit now" button appears once one is pending. Settings changes do not need a
  restart.

## UI

- **Layout:** adaptive grid, single-session (full-size), or stacked-vertically — toggle per preference; a
  **draggable sidebar** (180–480 px) shows every session's live status (busy / waiting / needs attention /
  done) with a keyboard session switcher (Ctrl+Arrow, configurable).
- **Transcript:** markdown rendering (headings, syntax-coloured code blocks, lists), tool calls collapse to
  headers with results underneath, JSON results get a copy button, emoji render, text is selectable, images
  pasted from the clipboard drop straight into the conversation.
- **TTY font:** adjustable family and size for the embedded terminal renderer (independent of the SDK
  transcript's own styling).
- **Toasts** for background events (plugin updates, notifications summary, etc.), an **About** dialog (app
  info, version, links), and minimize-to-tray on close.
- **Notifications:** an OS toast when you're present at the machine, or a **Discord webhook** when you're
  away (presence detected via idle time + lock state) — fires when a session transitions to "needs
  attention".

## Install / run

```
dotnet run --project src/Cockpit.App
```

Or build and launch the produced executable directly (double-click, or `Start-Process` from a script) — the
app is designed to run **detached**, with no attached console: all logging goes to a file logger rather than
stdout, since a detached launch has nothing to capture a console stream.

Create a session via **+ New session**: pick a profile, permission mode, model and effort. Settings live in
**`cockpit.json`** next to the app's config (`%APPDATA%\Cockpit\cockpit.json` by default) — notifications,
layout, voice, session-switch shortcut, per-profile defaults, always-allow rules, the MCP registry, and
plugin state all live in this one file.

Each **profile** can point at its own **`CLAUDE_CONFIG_DIR`** (e.g. a work vs. personal Claude account) —
left unset only when the profile matches the CLI's own default (`~/.claude`), otherwise exported per spawn so
the right login/config is used for that profile's sessions.

For UI development there is a headless verification mode that renders the main window off-screen and writes
a PNG (no display needed):

```
dotnet run --project src/Cockpit.App -- --screenshot out.png
```

Tests:

```
dotnet test
```

## Requirements

| Tool | Version |
|------|---------|
| .NET SDK | **10.0+** |
| Claude Code CLI (`claude`) | installed and logged in (`claude /login`) with an active subscription |

## Documentation

- **[Plugin SDK guide](docs/plugins/PLUGIN-SDK.md)** — build a plugin: settings, sidebar, dialogs, session
  providers, MCP server registration, packaging, install, and publishing your own plugin store.
- **[Plugin API reference](docs/plugins/API-REFERENCE.md)** — every method a plugin can call
  (`ICockpitHost`, `ICockpitActions`, `IPluginStorage`, `IPluginSettingsView`, the `Sessions` and `Mcp`
  namespaces), with signatures, parameters and short examples.
- **[Example store index](docs/plugins/example-store-index.json)** — a template for your own store's
  `index.json`.
- **Official plugin store:**
  [github.com/raymondkrahwinkel/AI-Cockpit-Plugins](https://github.com/raymondkrahwinkel/AI-Cockpit-Plugins)
  — the six [`plugins-dev/`](plugins-dev) example plugins, published.
- **Scaffold a new plugin:** `dotnet new install ./templates/cockpit-plugin` then
  `dotnet new cockpit-plugin -n My.Plugin -o plugins-dev/My.Plugin`.

## How this project is built

This project is developed in an **AI-assisted workflow** — fittingly, since it is itself a tool for working
with AI agents. The direction, architecture decisions, feature choices and acceptance testing are human; the
majority of the implementation is written by AI agents (Anthropic's Claude) orchestrated through Claude Code
by the maintainer.

That workflow does not lower the bar — it *is* the bar this tool exists to support:

- every change is reviewed, must build with **zero warnings**, and ships with unit tests
  (xUnit; several hundred and counting);
- UI changes are verified visually against rendered screenshots before they land;
- features that touch the real CLI are verified end-to-end against a live `claude` process, not
  assumed from documentation;
- the maintainer signs off on every commit and takes full responsibility for the result.

If you find something that looks generated-and-unread, that is a bug in the process — please open an issue.

## Disclaimer

AI-Cockpit is an independent open-source project. It is **not affiliated with, endorsed by, or sponsored by
Anthropic**. "Claude" and "Claude Code" are trademarks of Anthropic, PBC.

The cockpit launches the official Claude Code CLI under **your own login and subscription**; your use of
Claude through this tool is governed by your own agreement with Anthropic (Terms of Service / Usage Policy).
The tool is **single-user by design**: it is a local desktop app for driving your own sessions, not a
service, proxy or account-sharing layer.

## Support

- **Found a bug or have a feature request?** [Open an issue](https://github.com/raymondkrahwinkel/AI-Cockpit/issues).
- **Security issue?** Don't open a public issue — follow [`SECURITY.md`](SECURITY.md) to report it privately.

## Contributing

Contributions are welcome, but the bar is high for a one-person project. **Read
[`CONTRIBUTING.md`](CONTRIBUTING.md) before opening a pull request.** By participating you agree to the
[Code of Conduct](CODE_OF_CONDUCT.md).

## Licence

AI-Cockpit is licensed under the **GNU Affero General Public License v3.0** — see [`LICENSE`](LICENSE).
