# Cockpit.Server in a container

The headless Cockpit as an image: `ghcr.io/raymondkrahwinkel/cockpit-server`, built by
`.github/workflows/server-image.yml` after a container smoke (`deploy/smoke.sh`) passes. `compose.yaml` runs on any plain
Docker host and carries no environment-specific settings.

## Tags and deploying

- `:sha-<commit>` is immutable: **deploy on this**. `COCKPIT_TAG` has no default and `latest` is never published.
- `:main` is a rolling tag for reading, not for pulling on a timer. No Watchtower and no auto-pull: a restart ends
  the live agent sessions, and the stop budget is only 8 seconds.
- `:sha-<commit>-dev` adds the .NET SDK so agents can build and test. Same tags with a `-dev` suffix.

The package is **private**, so the host logs in once with a token that has `read:packages`:

    docker login ghcr.io -u <github-user>

## First start

1. Nothing to set up in `/state`: the server turns the node door on itself, and mints its certificate on the first
   start. If the node port is held, or the door does not listen for any other reason, the server logs why and exits 1
   rather than reporting `running`.
2. Put the two secrets in files, owner-only is fine (the entrypoint reads them as root):
   `secrets/unlock-password` and `secrets/connect-key` (at least 43 characters). Other paths: set
   `COCKPIT_UNLOCK_PASSWORD_PATH` and `COCKPIT_CONNECT_KEY_PATH`.
3. Optional `session.env` (or `COCKPIT_SESSION_ENV_FILE`): `GH_TOKEN` and anything else the agent sessions should inherit. These are plain
   environment variables, so `docker inspect` shows them; only the two secrets above are files.
4. `COCKPIT_TAG=sha-<commit> docker compose -f deploy/compose.yaml up -d`
5. In the app's settings, set the clone root to `/work/clones` and the worktree root to `/work/worktrees`. The defaults
   lie under `/state`, which an agent session cannot enter (see below).

## What it keeps

| Volume | Mount | Holds |
| --- | --- | --- |
| `state` | `/state` | `cockpit.json`, `node-certificate.pfx`, `node-lockouts.json`, the cockpit's transcripts, logs |
| `work` | `/work` | clones and worktrees, writable for the server and the agent sessions |
| `claude` | `/home/agent/.claude` and `/home/app/.claude` | the Claude Code login and transcripts |
| `codex` | `/home/agent/.codex` and `/home/app/.codex` | the Codex login and transcripts |
| `ssh` | `/home/app/.ssh` | the server's SSH keys (clones) |
| `agent-ssh` | `/home/agent/.ssh` | the agent sessions' SSH keys (their pushes); ssh refuses a key another user can read |

The same `state` volume across a `down` and `up` keeps the certificate, and with it the fingerprint.

## Reaching it

One port: **20383** (the node door, HTTPS with a self-signed certificate). The log names the certificate to pin:

    Node listener for ... presents certificate fingerprint <SHA-256 hex>.

`GET /healthz` on that port answers without a key: 200 or 503, with only each check's name and whether it holds. The
compose health check uses it. It turns 503 when the Workflows scheduler misses two ticks; without the Workflows plugin
there is no check and it stays 200. An expired sign-in is an alarm, not a reason to restart, so it never counts here.

There is no docker socket and no docker CLI in the image. `COCKPIT_STOP_BUDGET_SECONDS` (default 8) stays below
`stop_grace_period` (15 s).

## Open question

Credential encryption is off on a fresh state, and then the unlock password is not used (the server logs a warning).
With encryption on, the server starts only with the unlock secret, and exits 1 without it. Turning encryption on for a
headless first start is not solved here.

## Agent sessions run as `agent`

The server runs as `app`; every `claude` and `codex` it starts (sessions, sign-ins, login checks) runs as `agent`
(uid 1700) through a wrapper on `PATH` and one sudoers rule that allows exactly those two binaries. An agent session
cannot read `/state`, the secrets or the server's `/proc` entries; it keeps the session's environment (`COCKPIT_PANE_ID`,
the MCP settings, `GH_TOKEN`) and has its own `HOME`.

- The container starts as root only for its entrypoint, which copies the secrets to a tmpfs only `app` can read, closes
  `/run/secrets`, and then drops to `app` for good. Do not set `no-new-privileges`: the wrapper needs sudo.
- A profile with a pinned `ExecutablePath`, or a cockpit-managed CLI install, bypasses the wrapper and runs as `app`.
  Leave both empty on the server.
