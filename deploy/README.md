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
2. Put the connect key in `secrets/connect-key` (at least 43 characters), owner-only is fine (the entrypoint reads it as
   root). For another path, set `COCKPIT_CONNECT_KEY_PATH`.
3. `session.env` (or `COCKPIT_SESSION_ENV_FILE`), required but may be empty: `GH_TOKEN` and anything else the agent
   sessions should inherit, `KEY=VALUE` per line. It is a file like the secrets: it reaches the server and its sessions
   only, never the container's environment, so `docker inspect`, `docker exec` and the health check do not see it.
4. The brain's two files, both required, also when you are upgrading: `secrets/brain-rclone.conf` (may stay empty
   until you have the app password) and `brain-instructions.md`. See [The brain](#the-brain).
5. `COCKPIT_TAG=sha-<commit> docker compose -f deploy/compose.yaml up -d`

The clone and worktree roots start out at `/work/clones` and `/work/worktrees` (`COCKPIT_CLONE_ROOT`,
`COCKPIT_WORKTREE_ROOT`): the defaults lie under `/state`, which an agent session cannot enter. A root set in the app's
settings stays; point it somewhere under `/work`.

## What it keeps

| Volume | Mount | Holds |
| --- | --- | --- |
| `state` | `/state` | `cockpit.json`, `node-certificate.pfx`, `node-lockouts.json`, the cockpit's transcripts, logs |
| `work` | `/work` | clones and worktrees, writable for the server and the agent sessions |
| `claude` | `/home/agent/.claude` and `/home/app/.claude` | the Claude Code login and transcripts |
| `codex` | `/home/agent/.codex` and `/home/app/.codex` | the Codex login and transcripts |
| `ssh` | `/home/app/.ssh` | the server's own SSH keys |
| `agent-ssh` | `/home/agent/.ssh` | the SSH keys git uses, for clones and pushes alike (git runs as `agent`) |
| `brain` | `/home/agent/Nextcloud` (in `brain-sync`: `/data/Nextcloud`) | the assistant's brain, kept in sync with Nextcloud |
| `brain-state` | `/bisync`, in `brain-sync` only | the bisync listings |

The same `state` volume across a `down` and `up` keeps the certificate, and with it the fingerprint.

## Reaching it

One port: **20383** (the node door, HTTPS with a self-signed certificate). The log names the certificate to pin:

    Node listener for ... presents certificate fingerprint <SHA-256 hex>.

`GET /healthz` on that port answers without a key: 200 or 503, with only each check's name and whether it holds. The
compose health check uses it. It turns 503 when the Workflows scheduler misses two ticks; without the Workflows plugin
there is no check and it stays 200. An expired sign-in is an alarm, not a reason to restart, so it never counts here.

There is no docker socket and no docker CLI in the image. `COCKPIT_STOP_BUDGET_SECONDS` (default 8) stays below
`stop_grace_period` (15 s).

## Credentials on the volume

The server state on the `state` volume is unencrypted. Protect it through the host and volumes: ownership and access
rights, disk encryption, and encrypted backups. A state with encrypted credentials is refused at startup; turn
encryption off on the desktop before copying it to the server, or start from an empty `/state`.

## Agent sessions run as `agent`

The server runs as `app`; every `claude`, `codex` and `git` it starts (sessions, sign-ins, login checks, its own
clones, worktrees and commits) runs as `agent` (uid 1700) through a wrapper on `PATH` and one sudoers rule that allows
exactly those three binaries. Git is among them because an agent can plant hooks and config in a shared repository,
which would otherwise run as the server. The SSH keys and git credentials for clones therefore belong to `agent`
(`agent-ssh`, or `GH_TOKEN` in `session.env`). An agent session
cannot read `/state`, the secrets or the server's `/proc` entries; it keeps the session's environment (`COCKPIT_PANE_ID`,
the MCP settings, `GH_TOKEN`) and has its own `HOME`.

- The container starts as root only for its entrypoint, which copies the secrets to a tmpfs only `app` can read, closes
  `/run/secrets`, and then drops to `app` for good. Do not set `no-new-privileges`: the wrapper needs sudo.
- All sessions share the one `agent` uid, so a session can read another session's environment, MCP config and
  transcripts. The line drawn here is between the sessions and the server, not between sessions.
- The health check drops to `app` with an empty environment before it runs node, since Docker runs it as root.
- Only `agent` may run the real `claude`, `codex` and `git` (group `agent-run`), so a profile that pins one of them
  fails instead of running it as `app`. `/state/cli` belongs to root, so the server can neither install a managed CLI
  nor find one; an older install there is moved aside on start. A pin to any other program still runs as `app`: leave
  `ExecutablePath` empty on the server.

## The brain

An agent session on the server loads the same brain as on the desktop: `~/Nextcloud/Notes/AI-OS/Me.md` and what it
points to. The `brain-sync` service keeps that part of the AI-OS tree in the `brain` volume with `rclone bisync`
against Nextcloud, every 60 seconds (`COCKPIT_BRAIN_SYNC_INTERVAL`). Nextcloud stays the hub: the server is one more
device next to the desktop.

- **Only an include list comes over**, `brain-filter.txt` by default: `Me.md`, `Me-Reference.md`, `Memory/` and
  `AGENTS/`. Never sync the whole tree: it also holds `claude-credentials/.credentials.json`, a Claude login every agent
  session would then read. Another list: `COCKPIT_BRAIN_FILTER_PATH`. A remote without a list is not synced at all.
- **Sessions write back as `agent`.** `brain-sync` runs as uid 1700, so what it brings is the agent's, and a note a
  session writes in `Memory/` reaches the desktop on the next run. The server itself (`app`) cannot read the brain:
  it is owner-only for `agent` (directories 0700, files 0600), although `app` is in `agent`'s group.
- **A large deletion stops the run.** One run may delete at most 10% of the files on either side
  (`COCKPIT_BRAIN_SYNC_MAX_DELETE`); more, and bisync logs `Safety abort: too many deletes` and changes nothing, every
  run, until you act. A mistake: copy back what is missing (it never overwrites what is there), and the next run is
  normal again:

      docker compose -f deploy/compose.yaml run --rm --no-deps --entrypoint rclone brain-sync \
        --config /run/secrets/cockpit_brain_rclone copy nc:Notes/AI-OS /data/Nextcloud/Notes/AI-OS \
        --filter-from /etc/brain-sync/nc.filter --ignore-existing

  Meant: make the same deletion in Nextcloud, or run the bisync below once with `--force` instead of `--resync`.
- **A change on both sides is never lost.** Bisync keeps both versions, renamed to `<file>.conflict1` (Nextcloud's) and
  `<file>.conflict2` (the server's), on both sides. Merge them by hand and delete the copies.
- **No automatic `--resync`.** A resync deletes nothing, but wherever a file differs it takes Nextcloud's version over
  the server's. So only a fresh volume gets one: no bisync state and no files. After a failure the next run retries
  without it. The state lives in `brain-state`, a volume only `brain-sync` mounts, so no session can change or wipe
  it. When bisync says it needs one, or the brain has files but `brain-state` is empty,
  `brain-sync` logs `stopped: ... needs a manual --resync` and syncs nothing more. Copy what the server has that
  Nextcloud lacks, then run it once by hand:

      docker compose -f deploy/compose.yaml run --rm --entrypoint rclone brain-sync --config /run/secrets/cockpit_brain_rclone \
        bisync nc:Notes/AI-OS /data/Nextcloud/Notes/AI-OS --filter-from /etc/brain-sync/nc.filter \
        --workdir /bisync/nc --resync
      docker compose -f deploy/compose.yaml restart brain-sync

- **The app password is `brain-sync`'s alone.** It reaches that container as a secret file; the cockpit container, where
  an agent session has a shell, never sees it, and neither does the server. `brain-sync` cannot reach `/state` or the
  server's secrets, and publishes no port.

### Setting it up

1. **The app password.** In Nextcloud: Settings → Security → *Create new app password*, one for this server only.
   Obscure it without leaving it in the shell history (`obscure -` reads stdin, end with Ctrl-D):

       docker run --rm -i rclone/rclone:1.75.1 obscure -

   and write `secrets/brain-rclone.conf` (or `COCKPIT_BRAIN_RCLONE_PATH`):

       [nc]
       type = webdav
       url = https://<nextcloud>/remote.php/dav/files/<user>
       vendor = nextcloud
       user = <user>
       pass = <the obscured password>

   Obscured is not encrypted: the file is the secret. Compose keeps a secret file's owner and mode, and `brain-sync`
   runs as uid 1700, so: `sudo chown 1700:1700 secrets/brain-rclone.conf && sudo chmod 0400 secrets/brain-rclone.conf`.
   Never put the password in an environment variable or in `session.env`.

   **Until you have it:** an empty file is the placeholder (`: > secrets/brain-rclone.conf`). `brain-sync` then logs
   `no remote nc in the rclone config yet: idle` and does nothing. Fill it in later and
   `docker compose -f deploy/compose.yaml up -d --force-recreate brain-sync`.
2. **`AI_OS_ROOT`** in `session.env`, since `Me.md` asks for it: `AI_OS_ROOT=/home/agent/Nextcloud/Notes/AI-OS`.
3. **Which brain is which.** `brain-instructions.md` next to `compose.yaml` (or `COCKPIT_BRAIN_INSTRUCTIONS_PATH`) is
   mounted read-only as the agent's `~/.claude/CLAUDE.md` and `~/.codex/AGENTS.md`. It turns the assistant a project or
   profile names into a file; keep personal content out of the repository. It must be readable by uid 1700
   (`chmod 0644`). For example:

       When this session runs as Zyra, load ~/Nextcloud/Notes/AI-OS/Me.md and follow the instructions in it.

   Choose the assistant per project or profile on the server (its *Assistant* field), as on the desktop.

### A second brain (off by default)

Aura lives on another Nextcloud and is not synced. To add it, without code: a `[aura]` remote in the same
`brain-rclone.conf`, `COCKPIT_BRAIN_SYNCS="nc:Notes/AI-OS=Nextcloud/Notes/AI-OS aura:Shared/AI=Nextcloud-Synvolution/Shared/AI"`
(each pair is `remote:path=local`, one pair per remote name, local under `/data`, its first folder being the volume), and a
`compose.override.yaml` that adds a volume mounted at `/data/Nextcloud-Synvolution` in `brain-sync` and `brain-init`
(with that path added to `brain-init`'s `chown` and `chmod`) and at `/home/agent/Nextcloud-Synvolution` in `cockpit`, plus an
include list as a config with target `/etc/brain-sync/aura.filter`.
