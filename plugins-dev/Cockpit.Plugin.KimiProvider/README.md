# Kimi Code Provider (ACP)

Adds **Kimi Code** (Moonshot) as a session provider, driven over the **Agent Client Protocol** — `kimi acp`,
JSON-RPC 2.0 over stdio, one persistent subprocess for the whole session. Not a TTY pane: the cockpit speaks
the protocol itself, so it sees the stream, the tool calls and the permission requests rather than a rendered
terminal.

Setup (installing the CLI, authenticating, choosing a model, and what silently doesn't reach Kimi) is
documented in [`Docs/setup.md`](Docs/setup.md) — the same page this plugin ships in-app under its own Docs
tab.

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

## Three known limitations — none of them a bug to file

1. **A failed turn is indistinguishable from a successful one.** Kimi maps its internal `failed` reason onto
   the same `end_turn` stop reason it uses for success, so nothing on the wire says the turn broke. The
   provider does not claim a failure detection it does not have.
2. **No quota or cost reporting.** Kimi sends no usage on the wire. The context percentage is recovered by
   asking the CLI itself (`/usage`) and parsing its reply; there are no rate-limit windows to show, so that
   part of the session UI stays empty.
3. **A system prompt does not reach Kimi.** A profile identity, a project's instructions or an embedded run's
   briefing all arrive as the host's `cockpit.append-system-prompt`, and `kimi acp` has no parameter for it:
   the `_meta` it accepts on `session/new` is never read, and `--agent-file` only exists on an engine the ACP
   path does not use. Rather than let it vanish, the session says so once in the transcript when a prompt was
   supplied — put what the agent must know in your first message instead.

## Distribution

`PATH`-resolved for now, on purpose. The cockpit's managed-CLI installer understands `RawBinary` and `TarGz`
archives; Kimi publishes its standalone builds exclusively as `.zip`, and the npm package is a Node script
with a `postinstall` step rather than an executable. Adding `Zip` to the managed-CLI layer would be a host
change touching every provider, so it is not part of this plugin (AC-275).
