---
title: Settings
order: 10
summary: Local Docker access, consent, and the exec toggle. Remote hosts are not supported yet.
icon: 🐳
---

This plugin needs no account or token. Remote Docker hosts are not supported yet (AC-556).

## The local daemon {#endpoint}

The endpoint setting has been removed because it affected only SDK operations, while CLI and Compose
operations could target another daemon. Previously saved endpoint values are ignored without failing
to load the plugin. A warning is logged once during plugin initialization when a nonblank value exists;
the saved value is not included in the log.

SDK operations use Docker.DotNet's local default: `npipe://./pipe/docker_engine` on Windows and
`unix:///var/run/docker.sock` elsewhere. Docker.DotNet 3.125.15 does not read `DOCKER_HOST` or Docker
contexts. CLI and Compose operations still use the Docker CLI's environment and selected context.
Keep those pointed at the same local daemon; remote contexts are not supported by the plugin.

## Operations by transport {#operations}

| Transport | MCP operations |
| --- | --- |
| Docker.DotNet SDK | `daemon_info`, `list_containers`, `logs`, `list_images`, `inspect`, `stats`, `top`, `list_volumes`, `list_networks`, `pull_image`, `start_container`, `stop_container`, `restart_container`, `remove_container`, `exec`, `run_container`, `tag`, `remove_volume`, `prune` |
| Docker CLI | `build_image`, `cp`, `push` |
| Docker Compose CLI | `compose_config`, `compose_logs`, `compose_up`, `compose_down`, `compose_build`, `compose_ps`, `compose_restart` |

Container status and the status-bar Kill action also use the SDK.

## Consent mode {#consent-mode}

Always ask, Read-free and All-free remain available. A consent mode saved for the local default remains
valid. Approval tied to a previous external endpoint falls back to Always ask until you choose a mode
for the local daemon in settings.

## Exec and run — off by default {#allow-exec}

**Allow exec / run into containers** is off until you turn it on. With it off, agents can inspect
containers, read logs, and start/stop/restart what already exists, but cannot run an arbitrary command
inside a container or create one from an image. Enabling it also permits image builds and file copies.
These operations still require consent according to the selected consent mode.

## Check which daemon each transport uses {#wrong-daemon}

If the container list differs from your terminal, check `DOCKER_HOST` and the selected Docker context.
The SDK always uses the local default socket; setting a remote CLI context does not move SDK calls.
