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

1. Seed `/state` once, before the first start. The node door (`/api/v1`, port 20383) is off on a fresh state and has no
   environment switch, and the unlock password is only asked for once credential encryption is on. Both are
   settings in `cockpit.json`; the smoke seeds them with `tests/Cockpit.ServerSeed`:

       dotnet run --project tests/Cockpit.ServerSeed -- <absolute state dir> <unlock password file>

   then copy that directory into the `state` volume owned by uid 1654 (`app`).
2. Put the two secrets in files, readable by uid 1654 (mode 0644, or `chown 1654`):
   `secrets/unlock-password` and `secrets/connect-key` (at least 43 characters). Other paths: set
   `COCKPIT_UNLOCK_PASSWORD_PATH` and `COCKPIT_CONNECT_KEY_PATH`.
3. Optional `session.env` (or `COCKPIT_SESSION_ENV_FILE`): `GH_TOKEN` and anything else the agent sessions should inherit.
4. `COCKPIT_TAG=sha-<commit> docker compose -f deploy/compose.yaml up -d`

## What it keeps

| Volume | Mount | Holds |
| --- | --- | --- |
| `state` | `/state` | `cockpit.json`, `node-certificate.pfx`, `node-lockouts.json`, transcripts, clones, worktrees, logs |
| `claude` | `/home/app/.claude` | the Claude Code login |
| `codex` | `/home/app/.codex` | the Codex login |
| `ssh` | `/home/app/.ssh` | SSH keys for git |

The same `state` volume across a `down` and `up` keeps the certificate, and with it the fingerprint.

## Reaching it

One port: **20383** (the node door, HTTPS with a self-signed certificate). The log names the certificate to pin:

    Node listener for ... presents certificate fingerprint <SHA-256 hex>.

There is no docker socket and no docker CLI in the image. `COCKPIT_STOP_BUDGET_SECONDS` (default 8) stays below
`stop_grace_period` (15 s).

## Not here yet

Agent sessions run as the same user as the server and can read the unlock and connect-key files. Do not run bypass-mode
agents on it before the uid separation lands.
