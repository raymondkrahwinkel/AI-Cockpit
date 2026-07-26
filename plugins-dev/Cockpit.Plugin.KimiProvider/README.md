# Kimi Code Provider (ACP)

Adds **Kimi Code** (Moonshot) as a session provider, driven over the **Agent Client Protocol** — `kimi acp`,
JSON-RPC 2.0 over stdio, one persistent subprocess for the whole session. Not a TTY pane: the cockpit speaks
the protocol itself, so it sees the stream, the tool calls and the permission requests rather than a rendered
terminal.

## What you need

- The **`kimi` CLI** on this machine (`npm i -g @moonshot-ai/kimi-code`, needs Node ≥ 22.19). The plugin
  resolves it from `PATH`, from a path you pin in its settings, or from a cockpit-managed copy if one is ever
  installed — it does not install the CLI for you. See *Distribution* below for why.
- **Authentication**, either an API key (set in the plugin's settings; passed to the child as an environment
  variable, `KIMI_API_KEY` by default, never as a command-line argument) or the CLI's own cached login. The
  settings view has a **Login with Kimi account…** button that runs `kimi acp --login` for you.

## Settings

| Field | What it does |
|---|---|
| Kimi command / path | `kimi`, or an absolute path when it is not on `PATH`. The view resolves it while you type and shows what will actually run. |
| API key (optional) | Stored encrypted at rest by the host, like every other plugin secret. Leave it empty to rely on the CLI's cached login instead. |
| Default model (optional) | Applied once the session exists, best effort — a model Kimi does not offer is skipped rather than failing the start. |

Two further fields exist on the stored config for the rare case you need them, and are edited in
`cockpit.json` rather than in the dialog: `AuthEnvVar` (which variable the key is set under, `KIMI_API_KEY`
by default) and `WorkingDirectory` (fallback for sessions that do not bring their own; falls back further to
the cockpit's own directory).

## What works

Streaming text and thinking · tool calls with real **Allow/Deny** cards (Kimi blocks on the answer, so the
cockpit's permission UI is the actual gate) · the cockpit's MCP servers handed to the child · resuming (`session/resume`, never `session/load` — that variant
replays the whole history as fresh updates) an
earlier session · cancelling mid-turn · switching model, mode and thinking while the session runs · a context
percentage in the session header.

The host's `acceptEdits` permission mode maps to Kimi's **`default`** mode, not `yolo`. Kimi has no
edits-only tier and `yolo` switches permission requests off entirely, so the middle setting would silently
become free rein over the disk.

## Two known limitations — neither is a bug to file

1. **A failed turn is indistinguishable from a successful one.** Kimi maps its internal `failed` reason onto
   the same `end_turn` stop reason it uses for success, so nothing on the wire says the turn broke. The
   provider does not claim a failure detection it does not have.
2. **No quota or cost reporting.** Kimi sends no usage on the wire. The context percentage is recovered by
   asking the CLI itself (`/usage`) and parsing its reply; there are no rate-limit windows to show, so that
   part of the session UI stays empty.

## Distribution

`PATH`-resolved for now, on purpose. The cockpit's managed-CLI installer understands `RawBinary` and `TarGz`
archives; Kimi publishes its standalone builds exclusively as `.zip`, and the npm package is a Node script
with a `postinstall` step rather than an executable. Adding `Zip` to the managed-CLI layer would be a host
change touching every provider, so it is not part of this plugin (AC-275).
